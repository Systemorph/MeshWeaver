using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Every instance reports what it runs — the platform build it serves, the framework identity its
/// bundles are keyed on, the update policy it follows and every module it carries with that
/// module's pinned coordinate — to the control instance's fleet inbox, once the default install has
/// settled and then on a fixed cadence (<c>Hosting:ReportInterval</c>, one hour by default).
/// </summary>
/// <remarks>
/// <para>
/// An instance is the only party that can answer this about itself: the control instance cannot
/// read another installation's nodes, and giving it a way to would be a far larger permission
/// decision than a fleet inventory deserves. Before this service the answer was a Code node an
/// operator ran by hand (<c>Hosting/Script/report-modules</c> on the control instance) — which,
/// measured 2026-08-19, had never run on any instance. A report nobody sends is an inventory
/// nobody has; this service is the same report on a clock.
/// </para>
/// <para>
/// The module list is <see cref="InstanceComboReader"/>'s combo, the ONE reader of "what does this
/// instance carry" — the candidate-release gate verifies an image against the same read, so the
/// fleet page and the deploy gate can never disagree about an instance's modules. The wire shape is
/// the control instance's inbox contract (<c>Hosting/Modules</c> in MeshWeaver.Plugins): a JSON body
/// keyed on <c>event: module-inventory</c>, signed GitHub-style with HMAC-SHA256 over the RAW body
/// in <c>X-Hub-Signature-256</c>. Fields the inbox does not read yet (<c>commitSha</c>,
/// <c>frameworkIdentity</c>, <c>updatePolicy</c>, <c>reporter</c>) ride along; an older control
/// instance ignores them, a newer one shows them.
/// </para>
/// <para>
/// Inert without <c>Hosting:Deployment</c>: a report filed under the wrong deployment id overwrites
/// ANOTHER instance's inventory and the fleet page renders that as fact, so the service refuses to
/// guess. Without <c>Hosting:ReportTo</c> it IS the control instance and writes the record locally
/// under <c>{Hosting:OperationalSpace}/Modules/{deployment}</c>, exactly where a reported one lands.
/// Full reference: <c>Doc/Architecture/DeploymentInventory</c>.
/// </para>
/// </remarks>
public sealed class DeploymentReportService : IHostedService, IDisposable
{
    private readonly IMessageHub hub;
    private readonly ILogger<DeploymentReportService> logger;

    /// <summary>
    /// ONE channel for every report — the boot report, the hourly tick and an on-demand
    /// <see cref="Report"/> all enqueue here and run one at a time, in request order. Two reports in
    /// flight at once are a defect, not a race to tolerate: the older read can land LAST and the
    /// record then says what the instance carried a moment ago, not now (measured on this test's
    /// first CI run — the boot report read one seeded module, an explicit report read two, and the
    /// boot report's write arrived second).
    /// </summary>
    private readonly ISubject<Guid> requests = Subject.Synchronize(new Subject<Guid>());
    private readonly Subject<(Guid Id, Notification<DeploymentReportOutcome> Result)> results = new();

    public DeploymentReportService(IMessageHub hub, ILogger<DeploymentReportService> logger)
    {
        this.hub = hub;
        this.logger = logger;
        httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        http = hub.ServiceProvider.GetService<IHttpClientFactory>()?.CreateClient(HttpClientName) ?? SharedHttp;
        subscriptions.Add(requests
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(id => Observable.Defer(RunOnce).Materialize().Select(result => (id, result)))
            .Concat()
            .Subscribe(results.OnNext, results.OnError));
    }

    public const string DeploymentKey = "Hosting:Deployment";
    public const string ReportToKey = "Hosting:ReportTo";
    public const string SecretKey = "Hosting:ModuleReportSecret";
    public const string OperationalSpaceKey = "Hosting:OperationalSpace";
    public const string IntervalKey = "Hosting:ReportInterval";
    public const string DefaultOperationalSpace = "Ops";
    public const string EventName = "module-inventory";
    public const string InboxRoute = "/api/hooks/Hosting/Modules";
    public const string SignatureHeader = "X-Hub-Signature-256";
    public const string InventoryNodeType = "Hosting/ModuleInventory";
    /// <summary>
    /// The discriminator a LOCAL write stamps. Content written without a <c>$type</c> is stored
    /// perfectly and then materialises as NOTHING — the node keeps the whole document while every
    /// reader of the control instance's record type sees an empty record.
    ///
    /// <para>🚨 <b>It must name a REGISTERED CLR type, and for its first months it did not</b>
    /// (#3625). This was the literal <c>"ModuleInventoryContent"</c>, a name no type in the fleet
    /// carries — so the reading hub could not resolve it, the polymorphic converter degraded the
    /// value back to a raw <see cref="System.Text.Json.JsonElement"/>, and the node materialised as
    /// nothing anyway: exactly the outcome the paragraph above says this constant exists to
    /// prevent. Stamping a discriminator is only half the cure; the other half is that something
    /// can resolve it, which is why <see cref="DeploymentReport"/> is now registered in
    /// <c>AddPluginCatalogTypes</c> and this constant is derived from it with <c>nameof</c> rather
    /// than typed out. The old value was invisible to every instrument the platform had until the
    /// untyped-content shard gate was repaired and caught it on its first working run.</para>
    /// </summary>
    public const string InventoryContentType = nameof(DeploymentReport);
    /// <summary>
    /// The self-update policy node this reporter reads the policy off. The declaring type lives in
    /// Memex.Portal.Shared, which this assembly cannot reference; a parity test there keeps the two
    /// constants equal.
    /// </summary>
    public const string UpdatePolicyNodeType = "UpdatePolicy";
    public const string UpdatePolicyPartition = "Admin";
    public const string HttpClientName = "deployment-report";
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(60);
    private static readonly HttpClient SharedHttp = new() { Timeout = DeliveryBudget };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CompositeDisposable subscriptions = new();
    private readonly IIoPool httpPool;
    private readonly HttpClient http;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        subscriptions.Dispose();
        results.OnCompleted();
    }

    private void Start()
    {
        var settings = ReadSettings();
        if (settings.Deployment.Length == 0)
        {
            logger.LogInformation(
                "[DeploymentReport] inert: {Key} is not set, so this instance reports nothing. Set it to "
                + "the id of this instance's Hosting/Deployment record on the control instance to join "
                + "the fleet inventory.", DeploymentKey);
            return;
        }

        // The first report waits for the default install to settle — reporting mid-install would file
        // "this instance carries nothing" for an instance that is about to carry the baseline, and
        // the fleet page would render it as fact until the next tick.
        var autoRegistration = hub.ServiceProvider.GetService<InstanceAutoRegistrationService>();
        var defaultsDone = autoRegistration?.Completed.Take(1).Select(_ => Unit.Default)
            ?? Observable.Return(Unit.Default);

        subscriptions.Add(defaultsDone
            .SelectMany(_ => Observable.Timer(TimeSpan.Zero, settings.Interval, TaskPoolScheduler.Default))
            // Through the one channel like every report. A failed tick is a WARNING with the fault —
            // never a silent skip and never the end of the schedule: the next tick reports again.
            .Select(_ => Report().Catch((Exception exception) =>
            {
                logger.LogWarning(exception,
                    "[DeploymentReport] the report for {Deployment} failed; the next one runs in "
                    + "{Interval}.", settings.Deployment, settings.Interval);
                return Observable.Empty<DeploymentReportOutcome>();
            }))
            .Concat()
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                outcome => logger.LogInformation(
                    "[DeploymentReport] {Deployment}: {Delivery} — {Detail}",
                    settings.Deployment, outcome.Delivery, outcome.Detail),
                exception => logger.LogError(exception,
                    "[DeploymentReport] the schedule for {Deployment} ended; nothing reports until the "
                    + "next boot.", settings.Deployment)));
    }

    /// <summary>
    /// Compose and deliver ONE report now. Cold: subscribing enqueues the request on the service's
    /// single channel, so it runs after every report requested before it and never beside one.
    /// Emits the outcome — where the report went and the report itself — or errors with the fault.
    /// </summary>
    public IObservable<DeploymentReportOutcome> Report() =>
        Observable.Create<DeploymentReportOutcome>(observer =>
        {
            var id = Guid.NewGuid();
            var subscription = results
                .Where(r => r.Id == id)
                .Select(r => r.Result)
                .Dematerialize()
                .Take(1)
                .Subscribe(observer);
            requests.OnNext(id);
            return subscription;
        });

    private IObservable<DeploymentReportOutcome> RunOnce()
    {
        var settings = ReadSettings();
        if (settings.Deployment.Length == 0)
            return Observable.Return(DeploymentReportOutcome.Skipped(
                $"no deployment id — set {DeploymentKey} to this instance's Hosting/Deployment record id; "
                + "nothing was reported."));

        var comboReader = hub.ServiceProvider.GetRequiredService<InstanceComboReader>();
        return comboReader.Read()
            .Take(1)
            .Zip(ReadUpdatePolicy(), (combo, policy) => Compose(settings, combo, policy))
            .Zip(ReadAdoptedFrameworks(), (report, adopted) => report with
            {
                AdoptedFrameworkIdentities = adopted.Identities,
                AdoptedFrameworkInventoryComplete = adopted.Complete,
                Warnings = adopted.Complete ? report.Warnings
                    : report.Warnings.Add("adopted artifact inventory is incomplete; retention must not delete"),
            })
            .SelectMany(report => Deliver(settings, report));
    }

    private IObservable<(ImmutableList<string> Identities, bool Complete)> ReadAdoptedFrameworks() =>
        hub.ServiceProvider.GetRequiredService<AccessService>().RunAsSystem(() =>
            hub.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath)))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Take(1)
                .Timeout(ReadBudget)
                .Select(change =>
                {
                    var identities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                    foreach (var node in change.Items)
                    {
                        var definition = node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger)
                            ?? throw new InvalidOperationException($"NodeType adoption record {node.Path} could not be read");
                        if (!string.IsNullOrWhiteSpace(definition.CompiledFrameworkVersion))
                            identities.Add(definition.CompiledFrameworkVersion);
                    }
                    return (Identities: identities.OrderBy(id => id, StringComparer.Ordinal).ToImmutableList(), Complete: true);
                }))
            .Catch<(ImmutableList<string> Identities, bool Complete), Exception>(ex =>
            {
                logger.LogWarning(ex, "[DeploymentReport] adopted artifact inventory could not be completed; retention must not delete");
                return Observable.Return((Identities: ImmutableList<string>.Empty, Complete: false));
            });

    private DeploymentReport Compose(Settings settings, InstanceCombo combo, string? updatePolicy)
    {
        var warnings = ImmutableList.CreateBuilder<string>();
        if (!combo.IsComplete)
            warnings.Add("a module source could not be read — this inventory is INCOMPLETE");
        // The reader's gate-oriented caveats (provenance shape, missing discovery records, moving
        // refs) are about reproducing a combo, not about what runs; the read FAILURES are what a
        // fleet operator must see, and they arrive as the caveats that are not one of the two
        // constant ones.
        foreach (var caveat in combo.Caveats)
            if (caveat != InstanceComboReader.ProvenanceCaveat
                && caveat != InstanceComboReader.NoDiscoveryCaveat
                && caveat.StartsWith("Could not read", StringComparison.Ordinal))
                warnings.Add(caveat);
        if (combo.Modules.Count == 0)
            warnings.Add("no modules were found — unusual for a running portal");

        var platformVersion = PlatformBuildInfo.PlatformVersion;
        if (string.IsNullOrWhiteSpace(platformVersion))
            warnings.Add("could not read the platform version");
        if (string.IsNullOrWhiteSpace(settings.Host))
            warnings.Add("could not read this instance's home url (PluginCatalog:HomeUrl)");

        return new DeploymentReport
        {
            Deployment = settings.Deployment,
            InstanceId = Blank(settings.InstanceId),
            Host = Blank(settings.Host),
            PlatformVersion = Blank(platformVersion),
            CommitSha = Blank(PlatformBuildInfo.CommitHash),
            FrameworkIdentity = Blank(PrebuiltAssemblySeeder.LiveFrameworkMvid),
            UpdatePolicy = Blank(updatePolicy),
            SampledAt = Stamp(DateTimeOffset.UtcNow),
            Modules = combo.Modules.Select(ModuleOf).ToImmutableList(),
            Warnings = warnings.ToImmutable(),
        };
    }

    /// <summary>
    /// One row per module. A module recorded under BOTH shapes reports as GitSync (the sync is what
    /// moves it) carrying the install record's version; the control instance's fold keeps exactly
    /// that.
    /// </summary>
    private static ModuleReport ModuleOf(ModuleCoordinate module) => new()
    {
        Id = module.ModuleId,
        Repository = Blank(module.GitSync?.RepositoryUrl),
        Ref = Blank(module.GitSync?.Branch),
        Subdirectory = Blank(module.GitSync?.Subdirectory),
        CommitSha = Blank(module.GitSync?.LastSyncCommitSha),
        LastSyncedAt = module.GitSync?.LastSyncedAt is { } at ? Stamp(at) : null,
        ModuleVersion = Blank(module.Package?.ModuleVersion) ?? Blank(module.Package?.Version),
        Origin = module.GitSync is not null ? "GitSync" : "Package",
    };

    /// <summary>
    /// The self-update policy, read as a children listing of the Admin partition rather than a
    /// point read: an instance without the node (every non-portal host, every test mesh) must not
    /// trip the routing NotFound and its storm-breaker once an hour. Null when absent or unreadable.
    /// </summary>
    private IObservable<string?> ReadUpdatePolicy()
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var query = $"path:{UpdatePolicyPartition} scope:children nodeType:{UpdatePolicyNodeType}";
        return accessService
            .RunAsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
            .Take(1)
            .Timeout(ReadBudget)
            .Select(change => change.Items
                .Select(node => PolicyOf(node.Content))
                .FirstOrDefault(policy => policy is not null))
            .Catch((Exception exception) =>
            {
                logger.LogDebug(exception,
                    "[DeploymentReport] the update policy could not be read; the report carries none.");
                return Observable.Return<string?>(null);
            });
    }

    private string? PolicyOf(object? content)
    {
        if (content is null)
            return null;
        // Content arrives TYPED as often as raw. Serialize the CONCRETE runtime type with the mesh's
        // own options (never the object overload, which adopts a foreign type into this hub's
        // registry as a side effect of a read) and read the one property generically.
        var element = content is JsonElement je
            ? je
            : JsonSerializer.SerializeToElement(content, content.GetType(), hub.JsonSerializerOptions);
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("policy", out var policy))
            return null;
        return policy.ValueKind switch
        {
            JsonValueKind.String => Blank(policy.GetString()),
            JsonValueKind.Number => policy.GetRawText(),
            _ => null,
        };
    }

    private IObservable<DeploymentReportOutcome> Deliver(Settings settings, DeploymentReport report)
    {
        var body = JsonSerializer.Serialize(report, Json);

        if (settings.ReportTo.Length == 0)
            return WriteLocally(settings, report, body);

        if (settings.Secret.Length == 0)
        {
            // Loud: a control url without a signing secret is a misconfiguration, and the receiver
            // drops unsigned reports — sending one would only look like reporting.
            logger.LogWarning(
                "[DeploymentReport] {ReportToKey} is set but no signing secret is configured "
                + "({SecretKey}:{Deployment} or {SecretKey}); the receiver drops unsigned reports, so "
                + "nothing was sent.", ReportToKey, SecretKey, settings.Deployment, SecretKey);
            return Observable.Return(DeploymentReportOutcome.Skipped(
                $"{ReportToKey} is set but no signing secret is configured — nothing was sent.", report));
        }

        var url = settings.ReportTo.TrimEnd('/') + InboxRoute;
        var signature = Sign(body, settings.Secret);
        return httpPool.Invoke(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(SignatureHeader, signature);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new DeploymentReportRejectedException(
                    $"the report for {settings.Deployment} was REJECTED by {url}: "
                    + $"{(int)response.StatusCode} {response.ReasonPhrase} {text}".TrimEnd());
            }
            return DeploymentReportOutcome.Sent(
                $"{report.Modules.Count} module(s) to {url} ({(int)response.StatusCode})", report);
        });
    }

    private IObservable<DeploymentReportOutcome> WriteLocally(
        Settings settings, DeploymentReport report, string body)
    {
        var payload = JsonNode.Parse(body)!.AsObject();
        payload["$type"] = InventoryContentType;
        var node = new MeshNode(settings.Deployment, $"{settings.OperationalSpace}/Modules")
        {
            Name = $"{settings.Deployment} — {report.Modules.Count} module(s)",
            NodeType = InventoryNodeType,
            State = MeshNodeState.Active,
            Content = payload,
        };
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        // Infrastructure, not a user action: there is no principal on a clock tick, and the record is
        // the platform's own observation of itself.
        return accessService
            .RunAsSystem(() => meshService.CreateOrUpdateNode(node))
            .Take(1)
            .Timeout(DeliveryBudget)
            .Select(written => DeploymentReportOutcome.Written(
                $"{report.Modules.Count} module(s) at {written.Path}", report));
    }

    /// <summary>GitHub's webhook signature shape: <c>sha256=</c> + lowercase hex HMAC-SHA256 of the raw body.</summary>
    public static string Sign(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    public static string Stamp(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Settings ReadSettings()
    {
        var config = hub.ServiceProvider.GetService<IConfiguration>();
        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();
        string Setting(string key) => config?[key]?.Trim() ?? "";

        var deployment = Setting(DeploymentKey);
        // Per-deployment secret first, fleet-wide fallback — mirrors the receiver's resolution.
        var secret = deployment.Length > 0 ? Setting($"{SecretKey}:{deployment}") : "";
        if (secret.Length == 0)
            secret = Setting(SecretKey);
        var interval = TimeSpan.TryParse(Setting(IntervalKey), CultureInfo.InvariantCulture, out var parsed)
            && parsed > TimeSpan.Zero
                ? parsed
                : DefaultInterval;
        var ops = Setting(OperationalSpaceKey);
        return new Settings(
            deployment,
            Setting(ReportToKey),
            secret,
            ops.Length > 0 ? ops : DefaultOperationalSpace,
            interval,
            Blank(options.InstanceId) ?? Setting("PluginCatalog:InstanceId"),
            Blank(options.HomeUrl) ?? Setting("PluginCatalog:HomeUrl"));
    }

    private sealed record Settings(
        string Deployment,
        string ReportTo,
        string Secret,
        string OperationalSpace,
        TimeSpan Interval,
        string InstanceId,
        string Host);
}

/// <summary>The wire body of one report — the control instance's <c>Hosting/Modules</c> inbox contract.</summary>
public sealed record DeploymentReport
{
    public string Event { get; init; } = DeploymentReportService.EventName;
    public string Deployment { get; init; } = "";
    public string? InstanceId { get; init; }
    public string? Host { get; init; }
    public string? PlatformVersion { get; init; }
    public string? CommitSha { get; init; }
    public string? FrameworkIdentity { get; init; }
    /// <summary>Framework identities of the builds actually adopted by this instance's NodeTypes.</summary>
    public ImmutableList<string>? AdoptedFrameworkIdentities { get; init; }
    /// <summary>True only after a complete adoption-stamp read. Null identifies a legacy report.</summary>
    public bool? AdoptedFrameworkInventoryComplete { get; init; }
    public string? UpdatePolicy { get; init; }
    public string Reporter { get; init; } = "hosted";
    public string SampledAt { get; init; } = "";
    public ImmutableList<ModuleReport> Modules { get; init; } = [];
    public ImmutableList<string> Warnings { get; init; } = [];
}

public sealed record ModuleReport
{
    public string Id { get; init; } = "";
    public string? Repository { get; init; }
    public string? Ref { get; init; }
    public string? Subdirectory { get; init; }
    public string? CommitSha { get; init; }
    public string? LastSyncedAt { get; init; }
    public string? ModuleVersion { get; init; }
    public string Origin { get; init; } = "GitSync";
}

public enum DeploymentReportDelivery
{
    /// <summary>Nothing left this instance; <see cref="DeploymentReportOutcome.Detail"/> says why.</summary>
    Skipped,
    /// <summary>Written to this instance's own operational space — it IS the control instance.</summary>
    Written,
    /// <summary>Accepted by the control instance's inbox.</summary>
    Sent,
}

public sealed record DeploymentReportOutcome(
    DeploymentReportDelivery Delivery,
    string Detail,
    DeploymentReport? Report)
{
    public static DeploymentReportOutcome Skipped(string detail, DeploymentReport? report = null) =>
        new(DeploymentReportDelivery.Skipped, detail, report);
    public static DeploymentReportOutcome Written(string detail, DeploymentReport report) =>
        new(DeploymentReportDelivery.Written, detail, report);
    public static DeploymentReportOutcome Sent(string detail, DeploymentReport report) =>
        new(DeploymentReportDelivery.Sent, detail, report);
}

public sealed class DeploymentReportRejectedException(string message) : InvalidOperationException(message);
