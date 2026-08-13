using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Claims the framework generation this process runs, and — once the bake has settled — sweeps the
/// generations nothing runs any more.
///
/// <para><b>The claim starts first and lives longest.</b> It is asserted in
/// <see cref="StartAsync"/>, before any sweep on this pod or any other can possibly conclude that
/// this generation is free, and it is only released when the process ends. It is the ONLY thing
/// that proves a generation is still referenced (<see cref="AssemblyCacheGenerations"/>), so its
/// lifetime has to be the process's, not the sweep's.</para>
///
/// <para><b>The sweep runs once, behind the bake.</b> A rollout adds exactly one generation, so one
/// sweep per pod start is the matching cadence — and running it behind <see cref="PreWarmCompletion"/>
/// keeps a directory listing of a few thousand files on a network share off the same window as the
/// compiles. Where no pre-warm is registered there is no bake to wait for and it runs straight away.</para>
///
/// <para><b>Deliberately not leased.</b> Every other decision around the bake is serialised by
/// <see cref="NodeTypeBakeLease"/>, which fails OPEN — so a lease could never be the thing that makes
/// deletion safe, and pretending otherwise would move the safety argument somewhere it does not hold.
/// The sweep is safe because its PLAN is safe: it is idempotent, it only ever removes files that no
/// rule protects, and two pods running it concurrently compute the same answer.</para>
/// </summary>
public sealed class AssemblyCacheRetentionHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    AssemblyCacheRetention retention,
    ILogger<AssemblyCacheRetentionHostedService> logger) : IHostedService, IDisposable
{
    private IDisposable? _claim;
    private IDisposable? _sweep;
    private IDisposable? _startedRegistration;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Only the filesystem store accumulates generations: it is the one whose key carries the
        // framework tag (BlobAssemblyStore keys v{version} alone, so a new image overwrites rather
        // than accrues). Nothing to claim or sweep anywhere else.
        if (services.GetService<IAssemblyStore>() is not FileSystemAssemblyStore store)
        {
            logger.LogDebug(
                "AssemblyCacheRetention: no filesystem assembly store on this host — nothing to claim or sweep");
            return Task.CompletedTask;
        }

        var pool = services.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
        var root = store.RootDirectory;
        var tag = FileSystemAssemblyStore.FrameworkTag;

        // 🚨 BEFORE ANYTHING ELSE. A sweep — on this pod or a peer — reads claims to decide what is
        // unreferenced, so this process must be advertising the generation it runs before it can be
        // the subject of that decision.
        _claim = AssemblyCacheGenerations.Claim(
            root, tag, Environment.MachineName, retention, pool, logger);

        logger.LogInformation(
            "AssemblyCacheRetention: claiming framework {Tag} under {Root} as {Holder} "
            + "(refresh {Refresh}, ttl {Ttl}); sweep keeps {Keep} generation(s) / {MinAge} minimum age, "
            + "collection {Armed}",
            tag, root, Environment.MachineName, retention.ClaimRefreshInterval, retention.ClaimTtl,
            retention.KeepGenerations, retention.MinimumAge,
            retention.Delete ? "ARMED" : "NOT armed (report only)");

        _startedRegistration = lifetime.ApplicationStarted.Register(() => KickSweep(root, tag, pool));
        return Task.CompletedTask;
    }

    private void KickSweep(string root, string tag, IIoPool pool)
    {
        // Sequence behind the bake when there is one: the sweep is a few thousand stat() calls on a
        // network share and has no deadline, so it has no business sharing a window with the compiles.
        // Any settlement will do — a bake that errored or proved nothing still means the compile queue
        // has drained, which is the only thing the sweep is waiting for.
        var bake = services.GetService<PreWarmCompletion>()?.Settled.Select(_ => Unit.Default)
            ?? Observable.Return(Unit.Default);

        _sweep = bake
            .SelectMany(_ => AssemblyCacheGenerations.Sweep(root, tag, retention, pool, logger))
            .Subscribe(
                _ => { },
                ex => logger.LogWarning(ex,
                    "AssemblyCacheRetention: the sweep of {Root} faulted — nothing was collected, and "
                    + "the next pod start plans it again", root));
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
        _sweep?.Dispose();
        _sweep = null;
        // The claim goes last: while this process lives it must keep advertising its generation.
        _claim?.Dispose();
        _claim = null;
    }
}

/// <summary>Registration for the assembly-cache generation claim + retention sweep.</summary>
public static class AssemblyCacheRetentionExtensions
{
    /// <summary>Config key arming DELETION. Default <c>false</c> — the sweep reports and removes nothing.</summary>
    public const string DeleteConfigKey = "AssemblyCache:Retention:Delete";

    /// <summary>Config key overriding how many recent generations are kept regardless of claims.</summary>
    public const string KeepGenerationsConfigKey = "AssemblyCache:Retention:KeepGenerations";

    /// <summary>Config key overriding the minimum age (a <see cref="TimeSpan"/> string) before a generation may be collected.</summary>
    public const string MinimumAgeConfigKey = "AssemblyCache:Retention:MinimumAge";

    /// <summary>Config key overriding how long a generation claim counts as live (a <see cref="TimeSpan"/> string).</summary>
    public const string ClaimTtlConfigKey = "AssemblyCache:Retention:ClaimTtl";

    /// <summary>
    /// Register the generation claim and the retention sweep for a host that uses a
    /// <see cref="FileSystemAssemblyStore"/>. Safe on any host: without such a store the service
    /// does nothing.
    ///
    /// <para>Deletion is OFF unless <see cref="DeleteConfigKey"/> is explicitly <c>true</c>. Until
    /// then the sweep measures the cache and logs exactly what it WOULD remove — which is the
    /// evidence a deployment should arm it against, since the alternative is a first run that
    /// deletes on defaults nobody has checked against their own roll cadence.</para>
    /// </summary>
    public static IServiceCollection AddAssemblyCacheRetention(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(FromConfiguration(configuration));
        services.AddHostedService<AssemblyCacheRetentionHostedService>();
        return services;
    }

    /// <summary>
    /// Read the retention policy from configuration. Every key degrades to its default when absent
    /// or malformed — a typo in a knob must not arm deletion, and must not disarm the claim either.
    /// </summary>
    public static AssemblyCacheRetention FromConfiguration(IConfiguration configuration)
    {
        var retention = AssemblyCacheRetention.ReportOnly;

        if (bool.TryParse(configuration[DeleteConfigKey], out var delete) && delete)
            retention = retention with { Delete = true };
        if (int.TryParse(configuration[KeepGenerationsConfigKey], out var keep) && keep >= 1)
            retention = retention with { KeepGenerations = keep };
        if (TimeSpan.TryParse(configuration[MinimumAgeConfigKey], out var minimumAge)
            && minimumAge > TimeSpan.Zero)
            retention = retention with { MinimumAge = minimumAge };
        if (TimeSpan.TryParse(configuration[ClaimTtlConfigKey], out var ttl) && ttl > TimeSpan.Zero)
            retention = retention with { ClaimTtl = ttl };

        return retention;
    }
}
