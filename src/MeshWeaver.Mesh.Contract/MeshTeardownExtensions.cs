using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Deterministic teardown for a mesh root hub. Disposing a hub is reactive and
/// returns immediately (<c>IMessageHub.Dispose</c> kicks off the disposal
/// state machine); callers that go on to tear down the hub's service scope —
/// tests between <c>[Fact]</c>s, a silo on stop, a host on shutdown — must wait
/// for ALL of the hub's activity to finish first, or a late continuation resolves
/// a service from the already-disposed Autofac scope and throws
/// <see cref="ObjectDisposedException"/> ("LifetimeScope … has already been
/// disposed"). Unobserved, that surfaces as an xUnit "catastrophic failure" that
/// corrupts the rest of the run.
///
/// <para>"All of the hub's activity" is TWO things, and
/// <see cref="IMessageHub.DisposalCompleted"/> only covers the first:</para>
/// <list type="number">
/// <item>the action blocks + in-flight message round-trips (drained before
///   <see cref="IMessageHub.DisposalCompleted"/> fires), and</item>
/// <item>I/O offloaded onto the ThreadPool through <see cref="IIoPool"/> — which
///   runs independently of the action block and is NOT tracked by
///   <see cref="IMessageHub.DisposalCompleted"/>. <see cref="IoPoolRegistry.DrainAll"/>
///   cancels + joins that I/O — a live change-feed leaf never completes on its own, so a
///   wait-without-cancel would time out and let the scope dispose under it.</item>
/// </list>
///
/// <para>The VERY END of teardown is the <see cref="MeshTeardownSignal"/>: fired here, exactly
/// once, after every drain phase, carrying the <see cref="TeardownReport"/> of what (if
/// anything) survived. Anything that must not run before teardown truly ends — scope disposal,
/// node-ALC unload, the next test's mesh — subscribes to that signal (or uses the report these
/// methods return), never to <see cref="IMessageHub.DisposalCompleted"/> alone.</para>
/// </summary>
public static class MeshTeardownExtensions
{
    /// <summary>
    /// Disposes <paramref name="mesh"/> and awaits BOTH halves of its drain
    /// (<see cref="IMessageHub.DisposalCompleted"/> then the
    /// <see cref="IoPoolRegistry"/>), so the caller may safely dispose the
    /// service scope afterwards. Each wait is bounded by <paramref name="timeout"/>
    /// (a stuck action block or leaked I/O slot completes the wait rather than
    /// hanging teardown — the underlying bug surfaces elsewhere, e.g.
    /// <c>AnyHubQuiescingTimedOut</c> or a non-zero <see cref="IoPoolRegistry.TotalInFlight"/>).
    /// </summary>
    public static async Task<TeardownReport> TeardownAsync(this IMessageHub mesh, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        // Capture mesh-scoped teardown services while the scope is still ALIVE —
        // never resolve DI once disposal has begun (the scope may already be tearing down).
        var ioPools = mesh.ServiceProvider.GetService<IoPoolRegistry>();
        var asyncDisposeQueue = mesh.ServiceProvider.GetService<AsyncDisposeQueue>();
        var activities = mesh.ServiceProvider.GetService<ActivityTracker>();
        var teardownSignal = mesh.ServiceProvider.GetService<MeshTeardownSignal>();

        // (0) QUIESCE ACTIVITIES FIRST — before anything is disposed.
        //
        // An activity falls through all three phases below: its trigger returned as soon as the
        // activity existed, its command runs off-turn so it holds no grain turn, and
        // SubscribeThroughPool holds a pool permit for the SUBSCRIBE window only — so DrainAll()
        // joins nothing once the command continues past it. Measured: a 5s activity, and teardown
        // returned after 2028ms with the command still running.
        //
        // This waits, it does not cancel: the run finishes and writes its terminal ActivityLog
        // status. It must run BEFORE Dispose because those Append/Finish writes go back through
        // hubs that are still alive. Bounded, so a run that never settles surfaces as a timeout
        // rather than hanging teardown forever.
        if (activities is not null)
            await activities.WhenIdle.Timeout(timeout)
                .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
                .ToTask();

        mesh.Dispose();

        return await mesh.WaitForDisposalAndIoDrainAsync(ioPools, asyncDisposeQueue, timeout, teardownSignal);
    }

    /// <summary>
    /// The wait half of <see cref="TeardownAsync"/>, exposed for callers that
    /// already drive <see cref="System.IDisposable.Dispose"/> themselves (and keep their
    /// own progress/diagnostic loop around <see cref="IMessageHub.DisposalCompleted"/>).
    /// Pass the <see cref="IoPoolRegistry"/> + <see cref="AsyncDisposeQueue"/> captured
    /// BEFORE disposal began.
    ///
    /// <para>Three ordered phases, all BEFORE the service scope is disposed:</para>
    /// <list type="number">
    /// <item>await <see cref="IMessageHub.DisposalCompleted"/> — the synchronous/reactive
    ///   disposal: action blocks + message round-trips. Resources enqueue their async
    ///   cleanup onto the <see cref="AsyncDisposeQueue"/> during this phase.</item>
    /// <item>cancel + join the offloaded ThreadPool I/O the action block doesn't cover
    ///   (<see cref="IoPoolRegistry.DrainAll"/>).</item>
    /// <item>after all the sync stuff is disposed, give the
    ///   <see cref="AsyncDisposeQueue"/> a bounded quiesce budget to finish
    ///   (<see cref="AsyncDisposeQueue.DrainAsync"/>), then the caller closes the scope.</item>
    /// </list>
    /// </summary>
    /// <returns>
    /// The <see cref="TeardownReport"/> — what, if anything, survived the drains. The SAME report
    /// is fired on <paramref name="teardownSignal"/> (when one is passed), so out-of-band
    /// observers see the identical terminal state the orchestrating caller does. Callers must
    /// surface a dirty report (fail the test class, error-log the shutdown) — proceeding
    /// silently over live work is the use-after-unload SIGSEGV.
    /// </returns>
    public static async Task<TeardownReport> WaitForDisposalAndIoDrainAsync(
        this IMessageHub mesh, IoPoolRegistry? ioPools, AsyncDisposeQueue? asyncDisposeQueue,
        TimeSpan timeout, MeshTeardownSignal? teardownSignal = null)
    {
        // (1) Action blocks + message round-trips.
        await mesh.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .ToTask()
            .WaitAsync(timeout);

        // (2) Offloaded ThreadPool I/O — the half DisposalCompleted does not cover. CANCEL + JOIN
        //     synchronously: a live change-feed leaf never completes on its own, so the old
        //     WhenDrained() (WAIT-only, polled) would time out and let the scope dispose while the
        //     leaf still runs → its ThreadPool thread dereferences a collectible node ALC's freed
        //     metadata after unload → native use-after-unload SIGSEGV. DrainAll() cancels every leaf
        //     so it stops, then joins — no ToTask, no wait-without-cancel.
        var leakedIoLeaves = ioPools?.DrainAll() ?? 0;

        // (3) After all the sync stuff is disposed (and everyone has enqueued their
        //     async cleanup), quiesce the async dispose queue before the scope closes.
        var asyncDisposeClean = asyncDisposeQueue is null
            || await asyncDisposeQueue.DrainAsync(timeout);

        // (4) The terminal signal — the very end of teardown, all phases accounted. Fired AFTER
        //     the drains so a subscriber that proceeds on it (scope disposal, ALC unload, next
        //     test's mesh) never runs concurrently with surviving teardown work — and the report
        //     tells it when that guarantee could NOT be kept.
        var report = new TeardownReport(leakedIoLeaves, asyncDisposeClean);
        teardownSignal?.SignalCompleted(report);
        return report;
    }
}
