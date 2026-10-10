using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Timestamps this silo's stop, stage by stage, so a departing silo's own log says how long each
/// stage of its stop took and how busy the process was while it ran (issue #6392, log fingerprint
/// 97b80f6a91dbdb74).
///
/// <para><b>Why it exists.</b> Peers log <c>GrainCallCancellationManager ... TimeoutException</c>
/// when a cancellation batch sent to ONE silo gets no answer within Orleans' 30 s
/// <c>ResponseTimeout</c>. Two causes fit that line equally: the target was stopping (or was cut
/// mid-stop) and peers still listed it as not Dead, or the target was alive but starved. Nothing
/// in this repository tells them apart, because the silo's own stop wrote its progress to the log
/// at only three places (the routing hold, the pool drain, the mesh drain) and nowhere between
/// them, so a stop that spent a minute somewhere in the middle left no mark of where.</para>
///
/// <para><b>What it does.</b> One marker per lifecycle stage. Orleans stops stages in DESCENDING
/// order and starts the next only when every observer of the current one has returned, so the
/// moment a marker runs is the moment every higher stage has finished. Each marker logs one
/// Information line: the stage, the time since the first stop callback, the time since the previous
/// marker (that is, how long the stage above it took), the thread pool's thread count and queued
/// work, the total GC pause so far and whether the stop is graceful. Lined up against the peers'
/// fingerprint timestamps, the lines decide between the two causes; see the doc page
/// <c>Doc/Architecture/ReadingASiloStop</c>.</para>
///
/// <para><b>What it deliberately does not do.</b> It changes no behaviour: it holds nothing,
/// cancels nothing and resolves nothing from DI at stop time (the logger is injected at
/// construction). It logs at Information, not Error: a slow stage is evidence to read, not a
/// defect to file.</para>
/// </summary>
internal sealed class SiloStopTimeline(ILogger<SiloStopTimeline> logger)
    : ILifecycleParticipant<ISiloLifecycle>
{
    // Ticks of Stopwatch.GetTimestamp(): an ELAPSED time on one process, never a wall clock.
    // Instance state on a mesh-scoped singleton (never static). 0 means "not reached yet".
    private long firstStopTimestamp;
    private long previousStopTimestamp;

    /// <inheritdoc />
    public void Participate(ISiloLifecycle observer)
    {
        // Listed in Orleans' stop order (highest stage first) for the reader; the order Orleans
        // actually stops them in is decided by the stage numbers, not by this list.
        (string Name, int Stage)[] stages =
        [
            (nameof(ServiceLifecycleStage.Active), ServiceLifecycleStage.Active),
            (nameof(ServiceLifecycleStage.BecomeActive), ServiceLifecycleStage.BecomeActive),
            (nameof(ServiceLifecycleStage.GrainDeactivation), ServiceLifecycleStage.GrainDeactivation),
            (nameof(ServiceLifecycleStage.RuntimeServices), ServiceLifecycleStage.RuntimeServices),
            (nameof(ServiceLifecycleStage.RuntimeInitialize), ServiceLifecycleStage.RuntimeInitialize),
            (nameof(ServiceLifecycleStage.First), ServiceLifecycleStage.First),
        ];
        foreach (var (name, stage) in stages)
            observer.Subscribe($"{nameof(SiloStopTimeline)}.{name}", stage, new StageMarker(this, name));
    }

    private void Reached(string stage, bool graceful)
    {
        var now = Stopwatch.GetTimestamp();
        // The first marker to run is the stop's own start; every later one reports against it.
        Interlocked.CompareExchange(ref firstStopTimestamp, now, 0L);
        var sinceFirst = Stopwatch.GetElapsedTime(Interlocked.Read(ref firstStopTimestamp), now);
        var previous = Interlocked.Exchange(ref previousStopTimestamp, now);
        var sincePrevious = previous == 0L ? TimeSpan.Zero : Stopwatch.GetElapsedTime(previous, now);

        logger.LogInformation(
            "SiloStopTimeline: stage {Stage} reached {ElapsedMs} ms into the silo stop "
            + "({SincePreviousMs} ms after the previous stage); thread pool {Threads} threads, "
            + "{PendingWorkItems} pending work items; GC pause total {GcPauseMs} ms; graceful={Graceful}",
            stage,
            (long)sinceFirst.TotalMilliseconds,
            (long)sincePrevious.TotalMilliseconds,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            (long)GC.GetTotalPauseDuration().TotalMilliseconds,
            graceful);
    }

    // One observer per stage. Its stop only logs and returns a completed task, so it can never
    // delay the stop it is measuring.
    private sealed class StageMarker(SiloStopTimeline owner, string stage) : ILifecycleObserver
    {
        public Task OnStart(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnStop(CancellationToken cancellationToken)
        {
            owner.Reached(stage, !cancellationToken.IsCancellationRequested);
            return Task.CompletedTask;
        }
    }
}

/// <summary>DI wiring for <see cref="SiloStopTimeline"/>.</summary>
internal static class SiloStopTimelineExtensions
{
    /// <summary>
    /// Registers the silo stop timeline as a silo lifecycle participant. Idempotent. On an Orleans
    /// CLIENT host the participant is registered but never enumerated (a client has no silo
    /// lifecycle), so the registration is inert there, exactly like the routing hold's.
    /// </summary>
    /// <param name="services">The service collection to add the participant to.</param>
    /// <returns>The same service collection for further chaining.</returns>
    public static IServiceCollection AddSiloStopTimeline(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(SiloStopTimeline)))
            return services;
        services.AddSingleton<SiloStopTimeline>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp =>
            sp.GetRequiredService<SiloStopTimeline>());
        return services;
    }
}
