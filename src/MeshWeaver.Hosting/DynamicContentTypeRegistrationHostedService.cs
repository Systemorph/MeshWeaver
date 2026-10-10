using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Runs <see cref="DynamicContentTypeRegistrar.RegisterBakedTypes"/> once per process, in the
/// background, after the boot's bake barrier (<see cref="PreWarmCompletion"/>) has settled — any
/// settlement: a completed, faulted or not-applicable bake all mean the compile queue has drained
/// and the adopted bundles have landed, which is all this pass waits for.
///
/// <para>🚨 <b>Never on the readiness path.</b> <see cref="StartAsync"/> returns immediately, the
/// pass is kicked from <c>ApplicationStarted</c>, and nothing gates on it: a replica serves while it
/// runs, exactly as it did before it existed — it only closes, type by type, the gap a replica had
/// for the dynamic types whose instances it never activated
/// (<c>Doc/Architecture/DynamicContentTypeRegistration</c>).</para>
///
/// <para>Registered by <see cref="PreWarmServiceCollectionExtensions.AddDynamicTypePreWarming"/>,
/// beside the pre-warmer whose barrier it follows. On by default; <see cref="EnabledConfigKey"/>
/// = <c>false</c> is the escape hatch.</para>
/// </summary>
public sealed class DynamicContentTypeRegistrationHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<DynamicContentTypeRegistrationHostedService> logger) : IHostedService, IDisposable
{
    /// <summary>Config key that turns the pass off (default: on).</summary>
    public const string EnabledConfigKey = "PreWarm:RegisterContentTypes";

    /// <summary>
    /// Config key overriding the pause between two registrations, as a <see cref="TimeSpan"/>
    /// string. Default <see cref="DynamicContentTypeRegistrar.DefaultBetweenTypes"/>.
    /// </summary>
    public const string BetweenTypesConfigKey = "PreWarm:RegistrationBetweenTypes";

    /// <summary>How many skipped or failed types the summary NAMES before it counts the rest.</summary>
    private const int NamedReported = 25;

    private IDisposable? _startedRegistration;
    private IDisposable? _pass;

    // 🚨 Plugins#2799 — the boot registration window opens HERE, at construction: the host
    // resolves every hosted service before it starts any of them, so this precedes the boot-time
    // readers (measured: they degrade before the pre-warmer starts). Resolved optionally, like
    // everything else here — a host without the registry simply has no window. It is the same
    // instance the stream cache records into (registered beside it, read by the host's
    // content-types health check off this provider).
    private readonly ContentDegradationRegistry? _degradations = OpenRegistrationWindow(services);

    private static ContentDegradationRegistry? OpenRegistrationWindow(IServiceProvider services)
    {
        var degradations = services.GetService<ContentDegradationRegistry>();
        degradations?.DeferWarningsUntilRegistrationSettles();
        return degradations;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startedRegistration = lifetime.ApplicationStarted.Register(Kick);
        return Task.CompletedTask;
    }

    private void Kick()
    {
        var configuration = services.GetService<IConfiguration>();
        if (bool.TryParse(configuration?[EnabledConfigKey], out var enabled) && !enabled)
        {
            logger.LogInformation(
                "DynamicContentTypeRegistration: OFF ({Key}=false) — a dynamic NodeType's content type "
                + "registers on this replica only when one of its instances activates here",
                EnabledConfigKey);
            SettleDeferredWarnings(null);
            return;
        }

        var pacing = DynamicContentTypeRegistrar.DefaultBetweenTypes;
        var pacingRaw = configuration?[BetweenTypesConfigKey];
        if (!string.IsNullOrWhiteSpace(pacingRaw))
        {
            if (TimeSpan.TryParse(pacingRaw, out var parsed) && parsed >= TimeSpan.Zero)
                pacing = parsed;
            else
                logger.LogWarning(
                    "DynamicContentTypeRegistration: ignoring invalid {Key}='{Value}' — pacing stays {Default}",
                    BetweenTypesConfigKey, pacingRaw, pacing);
        }

        // Optional, like the pre-warmer's own resolution: AddDynamicTypePreWarming is documented as
        // safe on a host with no mesh hub, and there it is a no-op rather than a startup fault.
        var mesh = services.GetService<IMessageHub>();
        if (mesh is null)
        {
            logger.LogDebug("DynamicContentTypeRegistration: no mesh hub resolved — nothing to register");
            SettleDeferredWarnings(null);
            return;
        }
        // No barrier registered means no bake to wait for: run straight away.
        var settled = services.GetService<PreWarmCompletion>()?.Settled.Select(_ => System.Reactive.Unit.Default)
                      ?? Observable.Return(System.Reactive.Unit.Default);
        var startedAt = DateTimeOffset.MinValue;
        // Appended on the pass's own subscription only (Concat — one outcome at a time).
        var outcomes = ImmutableList<ContentTypeRegistrationOutcome>.Empty;

        _pass = settled
            .Take(1)
            .Do(_ => startedAt = DateTimeOffset.UtcNow)
            .SelectMany(_ => DynamicContentTypeRegistrar.RegisterBakedTypes(mesh, pacing, logger))
            .Subscribe(
                outcome =>
                {
                    outcomes = outcomes.Add(outcome);
                    if (outcome.Status is ContentTypeRegistrationStatus.Registered)
                        logger.LogDebug(
                            "DynamicContentTypeRegistration: registered {TypePath} → {ContentType}",
                            outcome.TypePath, outcome.Detail);
                },
                ex =>
                {
                    logger.LogWarning(ex,
                        "DynamicContentTypeRegistration: the pass FAULTED after {Count} type(s) — the rest "
                        + "register only when an instance activates on this replica, as before this pass existed",
                        outcomes.Count);
                    SettleDeferredWarnings(mesh);
                },
                () =>
                {
                    Summarise(outcomes, DateTimeOffset.UtcNow - startedAt, pacing);
                    SettleDeferredWarnings(mesh);
                });
    }

    /// <summary>
    /// Closes the boot registration window (Plugins#2799) and writes, ONCE per NodeType, the
    /// "stayed an untyped JsonElement" warning the read seams deferred — for exactly the types that
    /// are still untyped now that the pass has had its chance. Same wording and the same
    /// <see cref="Mesh.MeshNodeContentDegradedException"/> marker as the read seams, so a log query or the
    /// CI trace sink that counts them counts these too; the count and the window of the reads it
    /// covers are in the line, because one line now stands for every deferred read of the type.
    /// </summary>
    private void SettleDeferredWarnings(IMessageHub? mesh)
    {
        if (_degradations is null)
            return;
        var contentTypes = mesh?.ServiceProvider.GetService<Mesh.Services.IMeshContentTypeRegistry>()
                           ?? services.GetService<Mesh.Services.IMeshContentTypeRegistry>();
        // The seam is the one that read the path named (LastSeam), never the first seam paired with
        // the last path — a type can degrade through GetStream first and GetQuery later.
        foreach (var d in _degradations.SettleDeferredWarnings(contentTypes))
        {
            var seam = d.LastSeam ?? d.Seam;
            logger.LogWarning(
                new Mesh.MeshNodeContentDegradedException(seam, d.LastPath, d.NodeType, rawJson: null),
                "{Seam}: Content for {Path} stayed an untyped JsonElement after deserialization "
                + "(TypeRegistry lacks the $type discriminator) — the latest of {Count} read(s) of NodeType "
                + "{NodeType} that degraded between {First:o} and {Last:o}, during the boot registration "
                + "window (first through {FirstSeam}); the registration pass did not register it on this "
                + "replica: downstream 'Content is X'/'as X' consumers will fail (renders empty) until an "
                + "instance activates here",
                seam, d.LastPath, d.Count, d.NodeType, d.WindowStart, d.LastAt, d.Seam);
        }
    }

    private void Summarise(
        IReadOnlyList<ContentTypeRegistrationOutcome> outcomes, TimeSpan elapsed, TimeSpan pacing)
    {
        int Count(ContentTypeRegistrationStatus s) => outcomes.Count(o => o.Status == s);
        logger.LogInformation(
            "DynamicContentTypeRegistration: registration-only pass over {Total} dynamic NodeType(s) "
            + "done in {Elapsed} (pacing {Pacing}) — registered={Registered} alreadyRegistered={Already} "
            + "declaresNoContentType={None} notBaked={NotBaked} bytesMissing={Missing} staleBytes={Stale} "
            + "faulted={Faulted}. Nothing was compiled and no NodeType record was written.",
            outcomes.Count, elapsed, pacing,
            Count(ContentTypeRegistrationStatus.Registered),
            Count(ContentTypeRegistrationStatus.AlreadyRegistered),
            Count(ContentTypeRegistrationStatus.DeclaresNoContentType),
            Count(ContentTypeRegistrationStatus.NotBaked),
            Count(ContentTypeRegistrationStatus.BytesMissing),
            Count(ContentTypeRegistrationStatus.StaleBytes),
            Count(ContentTypeRegistrationStatus.Faulted));

        // Named, because a count of types left unregistered is not actionable: these are the types
        // whose content can still render empty on this replica until an instance activates here.
        var unresolved = outcomes
            .Where(o => o.Status is ContentTypeRegistrationStatus.BytesMissing
                or ContentTypeRegistrationStatus.StaleBytes
                or ContentTypeRegistrationStatus.Faulted)
            .ToList();
        if (unresolved.Count == 0)
            return;
        var named = string.Join(", ", unresolved.Take(NamedReported)
            .Select(o => $"{o.TypePath} ({o.Status}: {o.Detail})"));
        if (unresolved.Count > NamedReported)
            named += $", …and {unresolved.Count - NamedReported} more";
        logger.LogWarning(
            "DynamicContentTypeRegistration: {Count} baked dynamic NodeType(s) could NOT be registered on "
            + "this replica — their content stays untyped here until an instance activates here: {Types}",
            unresolved.Count, named);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _startedRegistration?.Dispose();
        _startedRegistration = null;
        _pass?.Dispose();
        _pass = null;
    }
}
