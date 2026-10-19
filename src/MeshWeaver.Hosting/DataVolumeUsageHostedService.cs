using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Walks the data volume behind the bake and then every <see cref="DataVolumeUsage.IntervalConfigKey"/>
/// (6 h by default), recording each reading on <see cref="DataVolumeUsageCensus"/> for the
/// <c>/health</c> check (MeshWeaver#6983).
///
/// <para><b>Read-only by construction.</b> It enumerates and stats; it never opens a file for
/// writing and never deletes. What to do about a full volume is the retention passes' decision and
/// an operator's — this is the reading they decide on.</para>
///
/// <para><b>Off the probe, on the I/O pool.</b> A walk of a large SMB share is minutes of metadata
/// reads, so it never runs inside <c>/health</c>: the check prints the LAST reading. It runs through
/// <see cref="IIoPool.InvokeBlocking{T}"/> on the <see cref="IoPoolNames.FileSystem"/> pool, sequenced
/// behind <see cref="PreWarmCompletion"/> where one is registered so it does not share the boot
/// window with the compiles, exactly as the assembly-cache sweep is.</para>
///
/// <para><b>A faulted pass is surfaced and bounded to that pass</b> — recorded on the census (the
/// check then reads Degraded) and logged at Warning — and the schedule continues.</para>
/// </summary>
public sealed class DataVolumeUsageHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    DataVolumeUsageCensus census,
    ILogger<DataVolumeUsageHostedService> logger) : IHostedService, IDisposable
{
    private IDisposable? startedRegistration;
    private IDisposable? schedule;

    /// <summary>The roots this host measures: the configured data paths, nested ones folded into their parent.</summary>
    public static ImmutableList<string> RootsOf(IConfiguration configuration) =>
        DataVolumeUsage.RootsOf(
            DataVolumeFreeSpace.PathConfigKeys.Select(key => configuration[key] ?? "")
                .Concat(configuration.GetSection(DataVolumeFreeSpace.PathsConfigKey).GetChildren()
                    .Select(child => child.Value ?? "")));

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var roots = RootsOf(configuration);
        census.SetRoots(roots);
        if (roots.IsEmpty)
            return Task.CompletedTask;
        var interval = DataVolumeUsage.IntervalOf(configuration[DataVolumeUsage.IntervalConfigKey]);
        logger.LogInformation(
            "DataVolumeUsage: measuring {Roots} after the bake settles and every {Interval} (read-only)",
            string.Join(", ", roots), interval);
        startedRegistration = lifetime.ApplicationStarted.Register(() => KickSchedule(roots, interval));
        return Task.CompletedTask;
    }

    private void KickSchedule(ImmutableList<string> roots, TimeSpan interval)
    {
        var bake = services.GetService<PreWarmCompletion>()?.Settled.Select(_ => Unit.Default)
                   ?? Observable.Return(Unit.Default);
        var pool = services.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
        var ticks = bake.Take(1).Concat(Observable.Timer(interval, interval).Select(_ => Unit.Default));
        schedule = ticks
            .Select(_ => roots.Select(root => Pass(root, pool)).Concat())
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger.LogWarning(ex, "DataVolumeUsage: the schedule STOPPED"));
    }

    private IObservable<Unit> Pass(string root, IIoPool pool) =>
        pool.InvokeBlocking(ct =>
        {
            Apply(census, root,
                () => DataVolumeUsage.Measure(root, DataVolumeUsage.MountPoints(), () => DateTimeOffset.UtcNow, ct),
                logger);
            return Unit.Default;
        });

    /// <summary>
    /// One pass's outcome onto the census — the whole decision, pure enough to drive from a test.
    ///
    /// <para>🚨 Only a COMPLETE reading replaces the previous one. A walk that threw (cancelled
    /// mid-way, an IO fault) and a reading that carries a <see cref="DataVolumeUsageReading.Fault"/>
    /// (the root vanished or could not be enumerated) both go the failure way: recorded as the
    /// root's fault — the check then reads Degraded and names it — logged at Warning, and the last
    /// real measurement stays the published one. Replacing it with "nothing was measured" or with a
    /// partial total would make the instrument lie about exactly the volume it exists to describe.</para>
    /// </summary>
    /// <param name="census">The census to update.</param>
    /// <param name="root">The root, by any spelling; it is keyed canonically.</param>
    /// <param name="measure">The walk.</param>
    /// <param name="logger">Where a failed pass is reported.</param>
    public static void Apply(
        DataVolumeUsageCensus census, string root, Func<DataVolumeUsageReading> measure, ILogger? logger)
    {
        DataVolumeUsageReading reading;
        try
        {
            reading = measure();
        }
        catch (Exception ex)
        {
            census.RecordFault(root, ex);
            logger?.LogWarning(ex,
                "DataVolumeUsage: the walk of {Root} did not complete — the previous reading (if any) stays, "
                + "the next pass walks again", root);
            return;
        }
        if (reading.Fault is not null)
        {
            census.RecordFault(root, reading.Fault);
            logger?.LogWarning(
                "DataVolumeUsage: {Root} could not be enumerated ({Fault}) — the previous reading (if any) stays, "
                + "the next pass walks again", root, reading.Fault);
            return;
        }
        census.Record(reading);
        logger?.LogInformation("DataVolumeUsage: {Reading}", DataVolumeUsage.Describe(reading));
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

/// <summary>Registration for the data-volume usage census.</summary>
public static class DataVolumeUsageExtensions
{
    /// <summary>Register the census singleton and the background walk. Safe on any host: with no
    /// data path configured nothing is walked and the census says so.</summary>
    public static IServiceCollection AddDataVolumeUsageCensus(this IServiceCollection services)
    {
        services.AddSingleton<DataVolumeUsageCensus>();
        services.AddHostedService<DataVolumeUsageHostedService>();
        return services;
    }
}
