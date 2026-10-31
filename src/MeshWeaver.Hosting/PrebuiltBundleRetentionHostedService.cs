using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>The last outcome of the prebuilt-bundle retention pass on this process — the status row operators read.</summary>
public sealed class PrebuiltBundleRetentionStatus
{
    private PrebuiltBundleSweepResult? last;
    private string? lastFault;

    /// <summary>The last pass's result, or null when none has run yet.</summary>
    public PrebuiltBundleSweepResult? Last => Volatile.Read(ref last);

    /// <summary>Why the last pass faulted before producing a result, or null.</summary>
    public string? LastFault => Volatile.Read(ref lastFault);

    internal void Record(PrebuiltBundleSweepResult result)
    {
        Volatile.Write(ref last, result);
        Volatile.Write(ref lastFault, null);
    }

    internal void RecordFault(Exception exception) =>
        Volatile.Write(ref lastFault, $"{exception.GetType().Name}: {exception.Message}");
}

/// <summary>
/// The recurring retention pass over the CI-published prebuilt-bundle store: once at boot, behind
/// the bake, and then every <see cref="PrebuiltBundleRetention.Interval"/>. Inert unless
/// <see cref="ShippedPrebuiltBundles.PublishedRootConfigKey"/> is configured.
///
/// <para><b>Where and why here.</b> Registered beside the modules GC
/// (<c>ConfigureMemexMesh</c> → <c>AddModuleGenerationsGc</c>): the two are sibling collectors of
/// the same data volume, and this one runs the same way — registered in <see cref="StartAsync"/>,
/// never run there; kicked from <c>ApplicationStarted</c>; blocking filesystem work on the
/// file-system <see cref="IIoPool"/>, never on a hub. It additionally waits for
/// <see cref="PreWarmCompletion"/> to settle, for the reason the assembly-cache sweep does: a
/// listing of a few thousand files on a network share has no business sharing a window with the
/// boot compiles.</para>
///
/// <para><b>The reference set is read fresh on every pass.</b> The NodeType records' adoption
/// stamps come from the mesh (a system-scoped, mesh-wide NodeType enumeration — the pre-warmer's
/// own reading); an enumeration that cannot be taken aborts THAT pass with nothing collected,
/// because a reference set that could not be read is incomplete, and incomplete licenses no
/// deletion.</para>
///
/// <para><b>Passes never overlap.</b> The ticks are serialised through <c>Concat</c>; a pass that
/// outlives the interval simply delays the next one. A faulted pass is logged, recorded on
/// <see cref="PrebuiltBundleRetentionStatus"/>, and the schedule continues — one bad listing must
/// not end the recurring process.</para>
///
/// <para>🚨 <b>…and never overlap ACROSS REPLICAS either (#7382).</b> Every replica of a store's owner
/// mounts the same store and runs this service, so each pass first takes the mesh-wide
/// <see cref="PrebuiltBundleRetentionLease"/>; a replica that does not hold it sweeps nothing and logs
/// who does. Without it, N replicas enumerated and deleted the same tree at once — one pass collected
/// while the others aborted on directories vanishing under their walk.</para>
/// </summary>
public sealed class PrebuiltBundleRetentionHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    PrebuiltBundleRetention retention,
    PrebuiltBundleRetentionStatus status,
    ILogger<PrebuiltBundleRetentionHostedService> logger) : IHostedService, IDisposable
{
    /// <summary>The budget for the mesh-wide NodeType enumeration that yields the adoption stamps.</summary>
    private static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(60);

    private IDisposable? startedRegistration;
    private IDisposable? schedule;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var root = services.GetService<IConfiguration>()?[ShippedPrebuiltBundles.PublishedRootConfigKey];
        if (!retention.Enabled)
        {
            logger.LogInformation(
                "PrebuiltBundleRetention: disabled ({Key}=false) — this instance reads a bundle store it does not own "
                + "(a consumer of the shared artifact store); the store's owner runs the one sweep",
                PrebuiltBundleRetentionExtensions.EnabledConfigKey);
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(root))
        {
            logger.LogDebug(
                "PrebuiltBundleRetention: no published bundle root configured ({Key}) — nothing to retain or collect",
                ShippedPrebuiltBundles.PublishedRootConfigKey);
            return Task.CompletedTask;
        }
        logger.LogInformation(
            "PrebuiltBundleRetention: sweeping {Root} at boot and every {Interval}; keeps unreferenced artifacts for at least {MinimumAge}, "
            + "{Grace} grace for an unsealed publication, "
            + "collection {Armed}",
            root, retention.Interval, retention.MinimumAge, retention.UnsealedGrace,
            retention.Delete ? "ARMED" : "NOT armed (report only)");
        startedRegistration = lifetime.ApplicationStarted.Register(() => KickSchedule(root));
        return Task.CompletedTask;
    }

    private void KickSchedule(string root)
    {
        var bake = services.GetService<PreWarmCompletion>()?.Settled.Select(_ => Unit.Default)
                   ?? Observable.Return(Unit.Default);
        var pool = services.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
        var ticks = bake.Take(1).Concat(Observable.Timer(retention.Interval, retention.Interval).Select(_ => Unit.Default));

        schedule = ticks
            .Select(_ => GuardedPass(Observable.Defer(() => RunPass(root, pool)), status.Record, ex =>
            {
                status.RecordFault(ex);
                PassLedger.ReportTerminal(PrebuiltBundleStore.PassName, logger, ex,
                    ": the pass over {Root} faulted — nothing was collected; the next pass plans it again",
                    root);
            }, cancelled =>
                // Not a fault and not "nothing was collected": the pool ended the pass (teardown).
                // The pass has already recorded its INTERRUPTED outcome (and, during collection,
                // written what it removed to the ledger); this line adds no second verdict.
                PassLedger.ReportTerminal(PrebuiltBundleStore.PassName, logger, cancelled,
                    ": the pass over {Root} faulted — nothing was collected; the next pass plans it again",
                    root)))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger.LogWarning(ex, "PrebuiltBundleRetention: the schedule over {Root} STOPPED", root));
    }

    private IObservable<PrebuiltBundleSweepResult> RunPass(string root, IIoPool pool)
    {
        // 🚨 Single-writer over the SHARED store (#7382): only the replica holding the lease sweeps.
        // No mesh hub means no lease can be taken, and an unleased sweep is exactly the overlap the
        // lease exists to prevent — the pass aborts with nothing collected instead.
        var mesh = services.GetService<IMessageHub>();
        if (mesh is null)
            return Observable.Throw<PrebuiltBundleSweepResult>(new InvalidOperationException(
                "no mesh hub on this host — the retention lease cannot be taken, so this pass may not sweep "
                + "a store other replicas may be sweeping"));
        // The lease content is written from this mesh hub; register its type on the mesh's shared
        // registry so the writer and the lease node's hub agree on its name (idempotent).
        PrebuiltBundleRetentionLease.RegisterContentType(mesh.ServiceProvider);
        // The clock is read when the attempt is subscribed (RunLeased), not when the tick queued it.
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var replica = Environment.MachineName;
        return PrebuiltBundleRetentionLease.RunLeased(
            mesh, clock, PrebuiltBundleRetentionLease.HoldFor(retention.Interval), replica,
            () => PassOver(StampedIdentities(), PinnedReferences(), (stamps, pinned) =>
                PrebuiltBundleStore.Sweep(
                    root,
                    PrebuiltAssemblySeeder.LiveFrameworkMvid,
                    PrebuiltAdoptionPolicy.RunningPlatformVersion,
                    stamps,
                    pinned,
                    retention,
                    pool,
                    logger)),
            verdict =>
            {
                if (verdict.Lease is null)
                    logger.LogWarning(
                        "PrebuiltBundleRetention: {Replica} skipped this pass over {Root} — the lease at {Lease} "
                        + "could not be read, so NO replica may sweep on it until it is repaired",
                        replica, root, PrebuiltBundleRetentionLease.LeasePath);
                else
                    // Warning, like the sweep outcome itself: the cluster ships Warning+ to Loki, and a
                    // replica that skipped writes no sweep outcome and no status — this once-per-pass
                    // line is the only production-visible evidence that it attempted and lost the lease.
                    logger.LogWarning(
                        "PrebuiltBundleRetention: {Replica} skipped this pass over {Root} — the lease at {Lease} is "
                        + "held by {Holder}, which runs the one sweep",
                        replica, root, PrebuiltBundleRetentionLease.LeasePath,
                        PrebuiltBundleRetentionLease.DescribeHolder(verdict.Lease));
            },
            // The sweep's outcome is recorded whatever the release does (#7459): a release that faults is
            // its own line, and the lease lapses at its hold's end — the pass is NOT reported as a fault.
            releaseFault => logger.LogWarning(releaseFault,
                "PrebuiltBundleRetention: {Replica} finished its pass over {Root} but could not release the lease at "
                + "{Lease} — the pass's outcome stands; the lease lapses at its hold's end, so the next sweep on any "
                + "replica waits for that instead of starting at once",
                replica, root, PrebuiltBundleRetentionLease.LeasePath));
    }

    /// <summary>
    /// One pass: the sweep runs only once BOTH reference sets were read. A reference read that
    /// errors (an unreadable adoption record among them — <see cref="NodeTypeAdoptionStamp"/>)
    /// terminates the pass with that error before <paramref name="sweep"/> is ever invoked, so
    /// nothing is deleted against an incomplete reference set.
    /// </summary>
    /// <typeparam name="TResult">The sweep's result.</typeparam>
    /// <param name="stamps">The framework identities the mesh's NodeType records adopted.</param>
    /// <param name="pinned">The platform builds something outside this process pins.</param>
    /// <param name="sweep">The sweep, given both complete reference sets.</param>
    /// <returns>The sweep's result, or the reference read's error.</returns>
    internal static IObservable<TResult> PassOver<TResult>(
        IObservable<ImmutableHashSet<string>> stamps,
        IObservable<ImmutableList<PinnedPlatformReference>> pinned,
        Func<ImmutableHashSet<string>, ImmutableList<PinnedPlatformReference>, IObservable<TResult>> sweep) =>
        stamps.Zip(pinned, (s, p) => (s, p)).SelectMany(refs => sweep(refs.s, refs.p));

    /// <summary>
    /// Bounds a pass's failure to THAT pass: a faulted pass is reported to <paramref name="fault"/>
    /// (surfaced — log + status — never swallowed) and completes, so the schedule's next tick
    /// re-reads everything from scratch instead of the subscription dying on the error.
    ///
    /// <para>🚨 A pass the I/O pool CANCELLED (an <see cref="OperationCanceledException"/> — the
    /// drain at silo teardown, MeshWeaver#7049) goes to <paramref name="cancelled"/>, never to
    /// <paramref name="fault"/>: it is not a fault, and the fault report's "nothing was collected"
    /// would contradict an interrupted pass that had already removed something.</para>
    /// </summary>
    /// <typeparam name="TResult">The pass's result.</typeparam>
    /// <param name="pass">One pass.</param>
    /// <param name="record">Receives a completed pass's result.</param>
    /// <param name="fault">Receives a faulted pass's error.</param>
    /// <param name="cancelled">Receives the cancellation of a pass the pool ended.</param>
    /// <returns>One <see cref="Unit"/> per pass, faulted, cancelled or not.</returns>
    internal static IObservable<Unit> GuardedPass<TResult>(
        IObservable<TResult> pass, Action<TResult> record, Action<Exception> fault,
        Action<OperationCanceledException> cancelled) =>
        pass
            .Do(record)
            .Select(_ => Unit.Default)
            .Catch<Unit, Exception>(ex =>
            {
                if (ex is OperationCanceledException oce)
                    cancelled(oce);
                else
                    fault(ex);
                return Observable.Return(Unit.Default);
            });

    /// <summary>
    /// Every platform build something outside this process pins — the union of every registered
    /// <see cref="PinnedPlatformReferenceSource"/> (the Deployment records and the registered
    /// instances' reports, on a control instance). Read fresh on every pass; a source that errors
    /// aborts the pass, because a pin set that could not be read is incomplete.
    /// </summary>
    private IObservable<ImmutableList<PinnedPlatformReference>> PinnedReferences()
    {
        var sources = services.GetServices<PinnedPlatformReferenceSource>().ToImmutableList();
        if (sources.IsEmpty)
            return Observable.Return(ImmutableList<PinnedPlatformReference>.Empty);
        return Observable.Concat(sources.Select(source => source().Take(1)))
            .ToList()
            .Select(lists => lists.SelectMany(l => l).ToImmutableList());
    }

    /// <summary>
    /// Every framework identity a NodeType record's <c>CompiledFrameworkVersion</c> names — the
    /// stamp <c>PrebuiltAssemblySeeder</c> writes on adoption (and every local compile writes
    /// too). Read as System, mesh-wide, exactly as the pre-warmer enumerates the same records.
    /// Errors propagate: an unreadable reference set aborts the pass.
    /// </summary>
    private IObservable<ImmutableHashSet<string>> StampedIdentities()
    {
        var mesh = services.GetService<IMessageHub>();
        var meshService = mesh?.ServiceProvider.GetService<IMeshService>();
        if (mesh is null || meshService is null)
            return Observable.Throw<ImmutableHashSet<string>>(new InvalidOperationException(
                "no mesh hub / IMeshService on this host — the NodeType adoption stamps cannot be read, "
                + "so the reference set is incomplete"));
        var access = mesh.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath)))
            .Take(1)
            .Timeout(EnumerationBudget)
            // 🚨 An unreadable record THROWS here (NodeTypeAdoptionStamp) and aborts the pass, as the
            // summary above promises — it used to read as "no stamp" and silently left the
            // reference set, so its bundle was eligible for deletion.
            .Select(change => StampedIdentitiesOf(change.Items, mesh.JsonSerializerOptions, logger)));
    }

    /// <summary>
    /// The reference set the NodeType records name. Throws on a record whose content is present
    /// but unreadable (its stamp is unknown) — never drops it; a declaration-less static NodeType
    /// (no content) adopts nothing and contributes nothing.
    ///
    /// <para>An EMPTY read throws too. A prebuilt bundle exists only because a NodeType was
    /// compiled, so a store holding bundles and a mesh answering ZERO NodeType records contradict
    /// each other — the read is incomplete (a catalog not yet hydrated, a partition not yet
    /// reachable), never "nothing is adopted". Sweeping against it would mark every unpinned
    /// bundle deletable; refusing costs one pass, and the next tick reads again.</para>
    /// </summary>
    /// <param name="records">NodeType records.</param>
    /// <param name="options">The reading hub's serializer options.</param>
    /// <param name="logger">Diagnostics for the content read.</param>
    /// <returns>Every adopted framework identity.</returns>
    internal static ImmutableHashSet<string> StampedIdentitiesOf(
        IReadOnlyCollection<MeshNode> records, System.Text.Json.JsonSerializerOptions options, ILogger? logger) =>
        NodeTypeAdoptionStamp.AdoptedIdentitiesOf(records, options, logger);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        startedRegistration?.Dispose();
        startedRegistration = null;
        schedule?.Dispose();
        schedule = null;
    }
}

/// <summary>Registration for the recurring prebuilt-bundle retention pass.</summary>
public static class PrebuiltBundleRetentionExtensions
{
    /// <summary>Config key disarming DELETION (<c>false</c> ⇒ report only). Default <c>true</c>.</summary>
    public const string DeleteConfigKey = "PreWarm:PrebuiltBundleRetention:Delete";

    /// <summary>
    /// Config key that turns the pass OFF entirely (<c>false</c> ⇒ never scheduled). Default <c>true</c>.
    /// Rendered <c>false</c> on a consumer of the shared artifact store (Doc/Architecture/SharedArtifactStore).
    /// </summary>
    public const string EnabledConfigKey = "PreWarm:PrebuiltBundleRetention:Enabled";

    /// <summary>Legacy count key retained for caller compatibility; it no longer changes retention.</summary>
    public const string KeepNewestPerSourceConfigKey = "PreWarm:PrebuiltBundleRetention:KeepNewestPerSource";

    /// <summary>Config key extending the minimum artifact age beyond 30 days.</summary>
    public const string MinimumAgeConfigKey = "PreWarm:PrebuiltBundleRetention:MinimumAge";

    /// <summary>Config key overriding the grace (a <see cref="TimeSpan"/> string) an unsealed publication is presumed in flight.</summary>
    public const string UnsealedGraceConfigKey = "PreWarm:PrebuiltBundleRetention:UnsealedGrace";

    /// <summary>Config key overriding the recurrence interval (a <see cref="TimeSpan"/> string).</summary>
    public const string IntervalConfigKey = "PreWarm:PrebuiltBundleRetention:Interval";

    /// <summary>
    /// Registers the recurring pass and its status. Safe on any host: without
    /// <see cref="ShippedPrebuiltBundles.PublishedRootConfigKey"/> the service does nothing.
    /// </summary>
    public static IServiceCollection AddPrebuiltBundleRetention(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(FromConfiguration(configuration));
        services.AddSingleton<PrebuiltBundleRetentionStatus>();
        services.AddPrebuiltBundleRetentionLease();
        services.AddHostedService<PrebuiltBundleRetentionHostedService>();
        return services;
    }

    /// <summary>
    /// Read the policy from configuration. Every key degrades to its default when absent or
    /// malformed — a typo in a knob must not silently change what is kept.
    /// </summary>
    public static PrebuiltBundleRetention FromConfiguration(IConfiguration configuration)
    {
        var retention = PrebuiltBundleRetention.Default;
        if (bool.TryParse(configuration[DeleteConfigKey], out var delete))
            retention = retention with { Delete = delete };
        if (bool.TryParse(configuration[EnabledConfigKey], out var enabled))
            retention = retention with { Enabled = enabled };
        if (TimeSpan.TryParse(configuration[MinimumAgeConfigKey], out var age) && age >= TimeSpan.FromDays(30))
            retention = retention with { MinimumAge = age };
        if (TimeSpan.TryParse(configuration[UnsealedGraceConfigKey], out var grace) && grace > TimeSpan.Zero)
            retention = retention with { UnsealedGrace = grace };
        if (TimeSpan.TryParse(configuration[IntervalConfigKey], out var interval) && interval > TimeSpan.Zero)
            retention = retention with { Interval = interval };
        return retention;
    }
}
