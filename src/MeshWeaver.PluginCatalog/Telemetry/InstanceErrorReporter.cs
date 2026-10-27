using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Delivers this instance's error digest to every registry it pulls from
/// (<c>Doc/Architecture/InstanceTelemetry</c>): on a clock, the <see cref="ErrorSignatureCollector"/>'s
/// NEW or ESCALATED signatures go to <see cref="ErrorDigestPayloads.Route"/> on each registry in
/// <see cref="PluginCatalogOptions.EffectiveRegistries"/>, presented with the same short-lived token
/// this instance pulls bundles with — so the registry files the digest under the instance it
/// AUTHENTICATED, never one the body claims. The registry turns each signature into a triage
/// submission and the fleet's dispatch takes it from there.
///
/// <para>Opt-out, never opt-in: <c>Telemetry:ErrorReports:Enabled=false</c> switches it off; the
/// default for a registered instance is on, because an instance nobody operates from a cluster
/// console has no other way to say it is failing. An instance with no registry credential sends
/// nothing and says so once. A digest is marked reported only once a registry ACCEPTED it, so a
/// refused or unreachable delivery costs nothing but a retry on the next tick.</para>
/// </summary>
public sealed class InstanceErrorReporter(
    IMessageHub hub, ErrorSignatureCollector collector, ILogger<InstanceErrorReporter> logger)
    : IHostedService, IDisposable
{
    /// <summary>The opt-out switch. Only the literal <c>false</c> turns reporting off.</summary>
    public const string EnabledKey = "Telemetry:ErrorReports:Enabled";

    /// <summary>The cadence, as a <c>TimeSpan</c>; floor one minute.</summary>
    public const string IntervalKey = "Telemetry:ErrorReports:Interval";

    /// <summary>Default <see cref="IntervalKey"/>.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    /// <summary>The first digest waits this long after the default install settled, so a boot's
    /// transient noise has a chance to stop recurring before it is reported.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);
    private static readonly HttpClient SharedHttp = new() { Timeout = DeliveryBudget };

    private readonly CompositeDisposable subscriptions = new();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => subscriptions.Dispose();

    /// <summary>Whether reporting is on for this instance (<see cref="EnabledKey"/>).</summary>
    public bool Enabled => IsEnabled(hub.ServiceProvider.GetService<IConfiguration>());

    internal static bool IsEnabled(IConfiguration? configuration) =>
        !string.Equals(configuration?[EnabledKey]?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan Interval(IConfiguration? configuration)
    {
        var raw = configuration?[IntervalKey]?.Trim();
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) && parsed >= TimeSpan.FromMinutes(1))
            return parsed;
        return DefaultInterval;
    }

    private void Start()
    {
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();
        if (!IsEnabled(configuration))
        {
            logger.LogInformation(
                "[InstanceErrorReport] switched off ({Key}=false): this instance reports no error digest to its registries.",
                EnabledKey);
            return;
        }
        var interval = Interval(configuration);
        var autoRegistration = hub.ServiceProvider.GetService<InstanceAutoRegistrationService>();
        var defaultsDone = autoRegistration?.Completed.Take(1).Select(_ => Unit.Default)
            ?? Observable.Return(Unit.Default);

        subscriptions.Add(defaultsDone
            .SelectMany(_ => Observable.Timer(FirstDelay, interval, TaskPoolScheduler.Default))
            .Select(_ => Report().Catch((Exception exception) =>
            {
                logger.LogWarning(exception,
                    "[InstanceErrorReport] the digest failed; the next one runs in {Interval}.", interval);
                return Observable.Empty<ErrorDigestOutcome>();
            }))
            .Concat()
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                outcome => logger.LogInformation("[InstanceErrorReport] {Delivery} — {Detail}", outcome.Delivery, outcome.Detail),
                exception => logger.LogError(exception,
                    "[InstanceErrorReport] the schedule ended; nothing reports until the next boot.")));
    }

    /// <summary>
    /// Compose and deliver ONE digest now. Cold; emits one outcome; errors only on a fault composing
    /// the digest (a delivery refusal is an outcome, not an error).
    /// </summary>
    public IObservable<ErrorDigestOutcome> Report() => Observable.Defer(ReportNow);

    /// <summary>The body of <see cref="Report"/>, evaluated at SUBSCRIPTION — so a tick queued behind
    /// a slow delivery reads the ledger after that delivery marked its signatures, not before.</summary>
    private IObservable<ErrorDigestOutcome> ReportNow()
    {
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();
        if (!IsEnabled(configuration))
            return Observable.Return(ErrorDigestOutcome.Skipped($"switched off ({EnabledKey}=false)"));
        var pending = collector.Pending();
        if (pending.IsEmpty)
            return Observable.Return(ErrorDigestOutcome.Skipped(
                collector.Dropped > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"nothing new ({collector.Count} signature(s) held, {collector.Dropped} dropped)")
                    : string.Create(CultureInfo.InvariantCulture, $"nothing new ({collector.Count} signature(s) held)")));

        var options = hub.ServiceProvider.GetService<PluginCatalogOptions>();
        var resolver = hub.ServiceProvider.GetService<RegistryTokenResolver>();
        if (options is null || resolver is null)
            return Observable.Return(ErrorDigestOutcome.Skipped("no plugin catalog on this mesh"));
        var stores = RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries)
            .Where(registry => Uri.TryCreate(registry.Url?.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .ToImmutableList();
        if (stores.IsEmpty)
            return Observable.Return(ErrorDigestOutcome.Skipped("no registry configured; nothing to report to"));

        var telemetry = hub.ServiceProvider.GetService<InstanceTelemetryReader>();
        var telemetryRead = telemetry is null
            ? Observable.Return<InstanceTelemetry?>(null)
            : telemetry.Read().Select(read => (InstanceTelemetry?)read);

        return telemetryRead
            .Take(1)
            .Select(read => ErrorDigestPayloads.Bound(new ErrorDigestPayloads.Request(read, DateTimeOffset.UtcNow, pending)))
            .SelectMany(request =>
            {
                var body = JsonSerializer.Serialize(request, ErrorDigestPayloads.Json);
                return stores
                    .Select(store => DeliverToStore(store, resolver, body))
                    .Merge()
                    .ToList()
                    .Select(deliveries =>
                    {
                        var accepted = deliveries.Where(d => d.Accepted).ToList();
                        if (accepted.Count > 0)
                            collector.MarkReported(request.Signatures);
                        var detail = string.Join("; ", deliveries.Select(d => d.Detail));
                        return accepted.Count > 0
                            ? ErrorDigestOutcome.Sent(request.Signatures.Count, detail)
                            : ErrorDigestOutcome.Refused(request.Signatures.Count, detail);
                    });
            });
    }

    private IObservable<StoreDelivery> DeliverToStore(PluginRegistryReference store, RegistryTokenResolver resolver, string body)
    {
        var url = store.Url.Trim().TrimEnd('/') + ErrorDigestPayloads.Route;
        var pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        var http = hub.ServiceProvider.GetService<IHttpClientFactory>()
            ?.CreateClient(InstanceRegistrationClient.BundleHttpClientName) ?? SharedHttp;
        return resolver.ResolveToken(store)
            .Take(1)
            .SelectMany(token =>
            {
                if (token.Length == 0)
                    return Observable.Return(new StoreDelivery(false,
                        $"{store.Url}: no credential for this registry, digest not delivered"));
                return pool.Invoke(async ct =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.TryAddWithoutValidation("Authorization", $"{InstanceKeys.Scheme} {token}");
                    using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                    var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return new StoreDelivery(false,
                            $"{url}: refused {(int)response.StatusCode} {response.ReasonPhrase} {Clip(text)}".TrimEnd());
                    return new StoreDelivery(true, $"{url}: accepted {Clip(text)}".TrimEnd());
                });
            })
            .Catch((Exception exception) =>
            {
                logger.LogWarning(exception,
                    "[InstanceErrorReport] the digest did not reach the registry {Url}; it is retried on the next tick.", url);
                return Observable.Return(new StoreDelivery(false, $"{url}: unreachable ({exception.GetType().Name})"));
            });
    }

    private static string Clip(string text)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 200 ? line : line[..200];
    }

    private sealed record StoreDelivery(bool Accepted, string Detail);
}

/// <summary>Where one digest went.</summary>
/// <param name="Delivery"><c>Skipped</c>, <c>Sent</c> or <c>Refused</c>.</param>
/// <param name="Signatures">How many signatures the digest carried.</param>
/// <param name="Detail">Why, or where.</param>
public sealed record ErrorDigestOutcome(string Delivery, int Signatures, string Detail)
{
    /// <summary>Nothing was sent, by design.</summary>
    public static ErrorDigestOutcome Skipped(string detail) => new("Skipped", 0, detail);

    /// <summary>At least one registry accepted it.</summary>
    public static ErrorDigestOutcome Sent(int signatures, string detail) => new("Sent", signatures, detail);

    /// <summary>No registry accepted it; everything stays pending.</summary>
    public static ErrorDigestOutcome Refused(int signatures, string detail) => new("Refused", signatures, detail);
}
