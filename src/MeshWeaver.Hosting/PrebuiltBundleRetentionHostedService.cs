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
        if (string.IsNullOrWhiteSpace(root))
        {
            logger.LogDebug(
                "PrebuiltBundleRetention: no published bundle root configured ({Key}) — nothing to retain or collect",
                ShippedPrebuiltBundles.PublishedRootConfigKey);
            return Task.CompletedTask;
        }
        logger.LogInformation(
            "PrebuiltBundleRetention: sweeping {Root} at boot and every {Interval}; keeps the newest {Keep} "
            + "pre-release identity(ies) per source and open line, {Grace} grace for an unsealed publication, "
            + "collection {Armed}",
            root, retention.Interval, retention.KeepNewestPerSource, retention.UnsealedGrace,
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
            .Select(_ => RunPass(root, pool)
                .Do(status.Record)
                .Select(_ => Unit.Default)
                .Catch<Unit, Exception>(ex =>
                {
                    // Surfaced (log + status), never swallowed — and the schedule survives it: the
                    // next tick re-reads everything from scratch.
                    status.RecordFault(ex);
                    logger.LogWarning(ex,
                        "PrebuiltBundleRetention: the pass over {Root} faulted — nothing was collected; the next pass plans it again",
                        root);
                    return Observable.Return(Unit.Default);
                }))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger.LogWarning(ex, "PrebuiltBundleRetention: the schedule over {Root} STOPPED", root));
    }

    private IObservable<PrebuiltBundleSweepResult> RunPass(string root, IIoPool pool) =>
        StampedIdentities().Zip(PinnedReferences(), (stamps, pinned) => (stamps, pinned))
            .SelectMany(refs =>
                PrebuiltBundleStore.Sweep(
                    root,
                    PrebuiltAssemblySeeder.LiveFrameworkMvid,
                    PrebuiltAdoptionPolicy.RunningPlatformVersion,
                    refs.stamps,
                    refs.pinned,
                    retention,
                    pool,
                    logger));

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
            .Select(change => change.Items
                .Select(n => n.ContentAs<NodeTypeDefinition>(mesh.JsonSerializerOptions, logger)?.CompiledFrameworkVersion)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToImmutableHashSet(StringComparer.Ordinal)));
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

    /// <summary>Config key overriding how many newest pre-release identities are kept per source and open line.</summary>
    public const string KeepNewestPerSourceConfigKey = "PreWarm:PrebuiltBundleRetention:KeepNewestPerSource";

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
        if (int.TryParse(configuration[KeepNewestPerSourceConfigKey], out var keep) && keep >= 0)
            retention = retention with { KeepNewestPerSource = keep };
        if (TimeSpan.TryParse(configuration[UnsealedGraceConfigKey], out var grace) && grace > TimeSpan.Zero)
            retention = retention with { UnsealedGrace = grace };
        if (TimeSpan.TryParse(configuration[IntervalConfigKey], out var interval) && interval > TimeSpan.Zero)
            retention = retention with { Interval = interval };
        return retention;
    }
}
