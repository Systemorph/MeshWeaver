using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
///   <see cref="IMessageHub.DisposalCompleted"/>. <see cref="IoPoolRegistry.DrainAll()"/>
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
        var logger = TeardownLogger(mesh);

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
        //
        // 🚨 SUBSCRIBED, not bridged, and BOUNDED BY A TOKEN rather than by a Timeout spliced into
        // the signal (#2301/#2488). The old shape was
        // `WhenIdle.Timeout(timeout).Catch(_ => Observable.Return(Unit.Default)).ToTask()`, which
        // is wrong three ways. (1) DEADLOCK: Rx completes a ToTask() TCS inline, so this method
        // resumed on whichever hub thread signalled idle — and the very next statement is
        // `mesh.Dispose()`, driven from that same thread. (2) The Catch folds "the budget expired
        // with a run still writing" into a value indistinguishable from "idle". (3) Once the Task
        // settles it can observe nothing, so a fault arriving afterwards has nowhere to go.
        // ObserveCompletion completes with RunContinuationsAsynchronously (no hub thread carries
        // us onward), cancelling the WAIT leaves the observation attached (a late fault is still
        // reported), and the expiry is DATA now (ActivitiesQuiesced on the report), not a silence.
        //
        // 🚨 LET THE WORK FINISH; KILL ONLY WHAT HAS STOPPED. `Quiesce` waits for every run that is
        // reporting progress, however long that takes inside `timeout`, and cancels only a run that
        // has made no progress for ActivityStallBudget — then abandons one that ignores even that.
        // Both are reported at ERROR (the red-log pipeline files an issue), with the activity path:
        // a run teardown had to kill did not finish its job, and that is a defect to find, never a
        // teardown detail to tolerate.
        var activitiesQuiesced = true;
        ActivityQuiesceReport quiesce = new([], []);
        if (activities is not null)
        {
            using var quiesceBudget = new CancellationTokenSource(timeout);
            try
            {
                quiesce = await activities.Quiesce(ActivityStallBudget).ObserveCompletion(
                    ex => logger?.LogError(ex,
                        "Mesh {Address}: the activity quiesce signal faulted AFTER teardown stopped "
                        + "waiting on it. Reported rather than orphaned; investigate the activity that "
                        + "faulted on its way to idle.", mesh.Address),
                    quiesceBudget.Token).ConfigureAwait(false) ?? quiesce;
                foreach (var path in quiesce.Cancelled)
                    logger?.LogError(ActivityCancelledByTeardown,
                        "Mesh {Address}: activity {ActivityPath} made no progress for {StallBudget} while the "
                        + "mesh was quiescing and was CANCELLED — its work did not finish. Find what it was "
                        + "waiting on; a run that reports progress is never cancelled.",
                        mesh.Address, path, ActivityStallBudget);
                foreach (var path in quiesce.Abandoned)
                    logger?.LogError(ActivityAbandonedByTeardown,
                        "Mesh {Address}: activity {ActivityPath} ignored the cancellation teardown handed it "
                        + "for another {StallBudget} and was ABANDONED — teardown proceeds over a run that "
                        + "observes neither progress nor cancellation. That run is the defect.",
                        mesh.Address, path, ActivityStallBudget);
            }
            catch (OperationCanceledException)
            {
                activitiesQuiesced = false;
                var inFlight = activities.InFlight;
                logger?.LogError(ActivitiesDidNotQuiesce,
                    "Mesh {Address}: activities did not reach idle within {Timeout} — teardown is "
                    + "proceeding over {Count} run(s) still in flight: [{Runs}]. A run still working at "
                    + "the host's teardown bound is reported, not killed; find why it takes this long.",
                    mesh.Address, timeout, inFlight.Count,
                    string.Join(" | ", inFlight.Select(r =>
                        $"{r.Label} (last progress {r.SinceLastProgress.TotalSeconds:F1}s ago"
                        + $"{(r.CancelRequested ? ", cancel requested" : string.Empty)})")));
            }
        }

        mesh.Dispose();

        // Hand the PRE-DISPOSE logger down: DrainAsync runs after mesh.Dispose(), where resolving
        // one is exactly the "never resolve DI once disposal has begun" mistake this method's own
        // header warns about. (Copilot review, #2527.)
        var report = await DrainAsync(
            mesh, ioPools, asyncDisposeQueue, timeout, teardownSignal, activitiesQuiesced, logger,
            quiesce)
            .ConfigureAwait(false);
        return report;
    }

    /// <summary>
    /// How long an activity may go without reporting progress during the pre-dispose quiesce
    /// before it is cancelled. A STALL budget, not a duration: a run that keeps logging is waited
    /// for up to the caller's whole teardown timeout. Matches the hub disposal stall budget and the
    /// I/O pool drain grace, so "no progress" means the same thing at every layer.
    /// </summary>
    public static readonly TimeSpan ActivityStallBudget = TimeSpan.FromSeconds(8);

    // Event ids so the red-log triage keys each shape on the log SITE (it ignores prose) — one
    // issue per shape, the prose free to carry the activity paths a reproduction needs.
    internal static readonly EventId ActivityCancelledByTeardown = new(7321, nameof(ActivityCancelledByTeardown));
    internal static readonly EventId ActivityAbandonedByTeardown = new(7322, nameof(ActivityAbandonedByTeardown));
    internal static readonly EventId ActivitiesDidNotQuiesce = new(7323, nameof(ActivitiesDidNotQuiesce));
    internal static readonly EventId IoLeavesCancelledAfterGrace = new(7324, nameof(IoLeavesCancelledAfterGrace));

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
    ///   (<see cref="IoPoolRegistry.DrainAll()"/>).</item>
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
    /// <param name="mesh">The mesh root hub whose disposal is being awaited.</param>
    /// <param name="ioPools">The <see cref="IoPoolRegistry"/> captured BEFORE disposal began.</param>
    /// <param name="asyncDisposeQueue">The <see cref="AsyncDisposeQueue"/> captured BEFORE disposal began.</param>
    /// <param name="timeout">Budget for each wait; an expiry surfaces as a
    /// <see cref="TimeoutException"/> (phase 1) or on the report, never as silence.</param>
    /// <param name="teardownSignal">Fired with the returned report, when supplied.</param>
    public static Task<TeardownReport> WaitForDisposalAndIoDrainAsync(
        this IMessageHub mesh, IoPoolRegistry? ioPools, AsyncDisposeQueue? asyncDisposeQueue,
        TimeSpan timeout, MeshTeardownSignal? teardownSignal = null) =>
        DrainAsync(mesh, ioPools, asyncDisposeQueue, timeout, teardownSignal, activitiesQuiesced: true,
            // No pre-dispose capture to inherit: this overload is entered AFTER the caller drove
            // Dispose() itself, so TeardownLogger's defensive resolve is the best available and a
            // dead scope degrades to "no logger" rather than throwing out of teardown.
            logger: TeardownLogger(mesh),
            quiesce: new ActivityQuiesceReport([], []));

    // 🚨 The public signature above is UNCHANGED on purpose. Threading the quiesce outcome through
    // as an extra optional parameter would have been source-compatible and BINARY-BREAKING — a
    // module compiled against the five-parameter method binds that exact signature and would fail
    // with MissingMethodException, which no source build in this repo can see (memory:
    // "A source build cannot see a BINARY break").
    private static async Task<TeardownReport> DrainAsync(
        IMessageHub mesh, IoPoolRegistry? ioPools, AsyncDisposeQueue? asyncDisposeQueue,
        TimeSpan timeout, MeshTeardownSignal? teardownSignal, bool activitiesQuiesced,
        ILogger? logger, ActivityQuiesceReport quiesce)
    {
        // (1) Action blocks + message round-trips.
        //
        // 🚨 SUBSCRIBED, never bridged with `.ToTask()` — and the FIRST reason is deadlock, not
        // fault observation. The previous shape was
        // `Catch(_ => Return(Unit)).FirstOrDefaultAsync().ToTask().WaitAsync(timeout)`, and Rx
        // completes a ToTask() TCS from inside the pipeline WITHOUT
        // RunContinuationsAsynchronously — so this method resumed INLINE on the thread that
        // signalled disposal, i.e. the mesh hub's own disposal thread. Everything below then ran
        // there, including `ioPools.DrainAll()`, which is a SYNCHRONOUS JOIN of every pooled I/O
        // leaf: the hub's own thread, parked, waiting for work that may need the hub. That is the
        // /async skill's Rule 1 in the teardown path. ObserveCompletion completes with
        // RunContinuationsAsynchronously, so phases (2)–(4) below can never run on a hub thread.
        //
        // The second reason is the one #2488 lists: the `Catch` turned a FAULTED disposal into an
        // indistinguishable success, and once the Task settled nothing was left to observe a fault
        // arriving afterwards. Now the fault is the answer when it comes first (recorded on the
        // report and logged), and is still REPORTED when it comes late, because the subscription
        // outlives the wait.
        Exception? disposalFault = null;
        using (var disposalBudget = new CancellationTokenSource(timeout))
        {
            try
            {
                await mesh.DisposalCompleted.ObserveCompletion(
                    ex => logger?.LogError(ex,
                        "Mesh {Address}: disposal faulted AFTER teardown stopped waiting on it. "
                        + "Reported rather than orphaned — an unobserved fault here is the "
                        + "UnobservedTaskException that poisons the next test class (#2301).",
                        mesh.Address),
                    disposalBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (disposalBudget.IsCancellationRequested)
            {
                // Preserve the shape callers already branch on: HubTestBase and MonolithMeshTestBase
                // both treat a TimeoutException here as HANG DETECTED and dump hub diagnostics.
                string diagnostics;
                try { diagnostics = mesh.GetDisposalDiagnostics(); }
                catch (Exception diagEx) { diagnostics = $"(disposal diagnostics unavailable: {diagEx.GetType().Name}: {diagEx.Message})"; }
                throw new TimeoutException(
                    $"Mesh {mesh.Address}: disposal did not complete within {timeout}. "
                    + "Something on the action block is not finishing — find it; do not widen the budget."
                    + Environment.NewLine + diagnostics);
            }
            catch (Exception ex)
            {
                // A disposal FAULT, which the old .Catch silently rendered as success. Carried on
                // the report and logged; see TeardownReport.DisposalFault for why it does not (yet)
                // flip Clean.
                disposalFault = ex;
                logger?.LogError(ex, "Mesh {Address}: disposal FAULTED — teardown continues, but the "
                    + "fault is now on the teardown report instead of being discarded.", mesh.Address);
            }
        }

        // (2) Offloaded ThreadPool I/O — the half DisposalCompleted does not cover. CANCEL + JOIN
        //     synchronously: a live change-feed leaf never completes on its own, so the old
        //     WhenDrained() (WAIT-only, polled) would time out and let the scope dispose while the
        //     leaf still runs → its ThreadPool thread dereferences a collectible node ALC's freed
        //     metadata after unload → native use-after-unload SIGSEGV. DrainAll() cancels every leaf
        //     so it stops, then joins — no ToTask, no wait-without-cancel.
        //
        //     🚨 The drain gives every leaf a GRACE to finish on its own before it cancels anything
        //     (IoPool.Drain): only a leaf that outlives the grace with its pool making no further
        //     progress is cancelled — and that is a KILL, reported at Error with the leaf's site,
        //     because the work it was doing did not finish.
        IReadOnlyList<IoPoolRegistry.PoolResidual> residualByPool = [];
        IReadOnlyList<IoPoolRegistry.PoolResidual> cancelledByPool = [];
        var leakedIoLeaves = ioPools is null ? 0 : ioPools.DrainAll(out residualByPool, out cancelledByPool);
        var cancelledIoLeaves = cancelledByPool.Sum(p => p.Residual);
        if (cancelledIoLeaves > 0)
            logger?.LogError(IoLeavesCancelledAfterGrace,
                "Mesh {Address}: the I/O drain had to CANCEL {Count} pooled leaf(es) that made no progress "
                + "within the drain grace — that work did not finish: [{Leaves}]. Find why each stalled; "
                + "a leaf that completes is never cancelled.",
                mesh.Address, cancelledIoLeaves, string.Join(", ", cancelledByPool));

        // (3) After all the sync stuff is disposed (and everyone has enqueued their
        //     async cleanup), quiesce the async dispose queue before the scope closes.
        var asyncDisposeClean = asyncDisposeQueue is null
            || await asyncDisposeQueue.DrainAsync(timeout).ConfigureAwait(false);

        // (4) The terminal signal — the very end of teardown, all phases accounted. Fired AFTER
        //     the drains so a subscriber that proceeds on it (scope disposal, ALC unload, next
        //     test's mesh) never runs concurrently with surviving teardown work — and the report
        //     tells it when that guarantee could NOT be kept.
        var report = new TeardownReport(leakedIoLeaves, asyncDisposeClean)
        {
            ResidualByPool = residualByPool,
            DisposalFault = disposalFault,
            ActivitiesQuiesced = activitiesQuiesced,
            CancelledActivities = quiesce.Cancelled,
            AbandonedActivities = quiesce.Abandoned,
            CancelledIoLeaves = cancelledIoLeaves,
            CancelledIoByPool = cancelledByPool,
        };
        teardownSignal?.SignalCompleted(report);
        return report;
    }

    /// <summary>
    /// The logger teardown reports through — resolved WHILE THE SCOPE IS STILL ALIVE, exactly like
    /// every other teardown service here, and null-tolerant so a mesh without logging still tears
    /// down. It is what makes a late fault REPORTED rather than swallowed: the observation arm has
    /// to land somewhere, and "somewhere" cannot be a service resolved after disposal began.
    /// </summary>
    private static ILogger? TeardownLogger(IMessageHub mesh)
    {
        try
        {
            return mesh.ServiceProvider.GetService<ILoggerFactory>()?
                .CreateLogger("MeshWeaver.Mesh.Teardown");
        }
        catch (ObjectDisposedException)
        {
            // The scope is already gone (the public WaitForDisposalAndIoDrainAsync is entered
            // AFTER the caller's own Dispose). Teardown must never fail on the act of preparing to
            // REPORT something — degrade to no logger. TeardownAsync avoids this entirely by
            // capturing before Dispose and handing the capture down.
            return null;
        }
    }
}
