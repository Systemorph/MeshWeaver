using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// The scheduling half of the thread watchers' HAND-ROLLED re-establish loops
/// (<c>ThreadExecution.InstallExecRoundWatcher</c>,
/// <c>ThreadExecution.InitializeThreadLifecycle</c>,
/// <c>ThreadSubmissionServer.InstallServerWatcher</c>).
///
/// <para>🚨 These three loops are NOT
/// <see cref="MeshWeaver.Mesh.ActivityControlPlaneExtensions.SubscribeWithReEstablish{T}"/>
/// and do NOT inherit its terminal fault classification (own-node-gone / poisoned
/// content): they re-establish on EVERY fault. Converting them is a separate change
/// — <c>InstallExecRoundWatcher</c> watches the PARENT hub's node, so the
/// address-scoped classification would need <c>parentHub.Address</c> to match at all.
/// This helper deliberately fixes only the SCHEDULING hazards the three loops shared;
/// it changes no classification behaviour.</para>
///
/// <para>Two hazards, both in the window where the hub is being torn down:</para>
/// <list type="number">
///   <item><description><b>The 1 s timer was armed after checking <c>disposed</c>, but the
///     TICK never re-checked it.</b> Arm-time is the wrong time: the whole point of the
///     delay is that a second passes. Teardown inside that second left a live tick that
///     re-subscribed a dead hub's stream — the #991 shape, a <c>TimerQueue</c> entry (a
///     strong GC root) holding <c>Establish</c> → the watcher closure → the hub. The
///     <c>SerialDisposable</c> cancels the schedule, but a tick already dispatched to the
///     pool still runs its handler, so the flag must be re-read at FIRE time. The
///     sanctioned helper does exactly this (<c>if (disposed) return;</c> at the head of
///     its own <c>Establish</c>).</description></item>
///   <item><description><b>A synchronous throw out of <c>establish</c> escaped onto the
///     timer's thread-pool thread.</b> Re-establishing resolves services off the hub's DI
///     scope (<c>GetWorkspace()</c> is <c>ServiceProvider.GetRequiredService&lt;IWorkspace&gt;()</c>,
///     and <c>GetMeshNodeStream().Subscribe()</c> resolves too); after teardown that scope
///     is gone and throws <see cref="ObjectDisposedException"/> straight out of
///     <c>Subscribe</c>. Rx does not swallow a throw from an <c>OnNext</c> handler — on the
///     default (thread-pool) scheduler it becomes an UNHANDLED exception and takes the
///     process down. Every fault must reach a graceful sink instead.</description></item>
/// </list>
/// </summary>
internal static class ReEstablishSchedule
{
    /// <summary>
    /// The production re-establish delay. Long enough to hop off the synchronous error
    /// stack (a Subscribe-time fault re-entering <c>Establish</c> inline recurses to a
    /// stack overflow) and to let the disposal hook set the <c>disposed</c> flag; short
    /// enough that a genuinely transient fault leaves the thread unobserved only briefly.
    /// Matches <c>SubscribeWithReEstablish</c>'s default.
    /// </summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Arms a single delayed re-establish and hands back the handle that CANCELS it.
    /// Assign the result to the watcher's <c>pendingReEstablish</c>
    /// <see cref="SerialDisposable"/> so teardown drops the pending
    /// <c>TimerQueue</c> entry (#991); assigning into an already-disposed
    /// <see cref="SerialDisposable"/> disposes the schedule immediately, which closes
    /// the arm-vs-teardown race.
    /// </summary>
    /// <param name="isDisposed">Reads the watcher's teardown flag. Evaluated at ARM time
    /// AND again when the timer FIRES — the second read is the fix.</param>
    /// <param name="establish">Re-subscribes the watcher's source. May throw synchronously
    /// (a disposed DI scope does); the throw is contained here, never on the timer thread.</param>
    /// <param name="logger">Sink for the contained fault. Optional, as everywhere else in
    /// these watchers.</param>
    /// <param name="context">Short watcher name for the fault log, e.g. <c>ExecRoundWatcher</c>.</param>
    /// <param name="threadPath">Thread path for the fault log.</param>
    /// <param name="scheduler">Test seam. Production default is
    /// <see cref="DefaultScheduler"/>, i.e. the same thread-pool timer the loops used
    /// before.</param>
    /// <returns>A handle that cancels the pending re-establish, or
    /// <see cref="Disposable.Empty"/> when the watcher is already torn down and nothing
    /// was armed.</returns>
    internal static IDisposable Arm(
        Func<bool> isDisposed,
        Action establish,
        ILogger? logger,
        string context,
        string threadPath,
        IScheduler? scheduler = null)
    {
        // Nothing to arm once the watcher is gone — the fault is then permanent (the
        // scope is gone), so retrying is futile AND would root the hub for a second.
        if (isDisposed())
            return Disposable.Empty;

        return Observable.Timer(Delay, scheduler ?? DefaultScheduler.Instance)
            .Subscribe(_ =>
            {
                // 🚨 RE-check at FIRE time. A second passed since the arm-time check, and
                // teardown very likely happened inside it — teardown is exactly when the
                // watched stream faults. A tick already dispatched to the pool runs even
                // though the SerialDisposable cancelled the schedule, so this read is the
                // only thing standing between a disposed hub and a fresh subscription.
                if (isDisposed())
                    return;
                try
                {
                    establish();
                }
                catch (ObjectDisposedException ex)
                {
                    // The teardown race the flag alone cannot close: `disposed` is still
                    // false (the hub's disposal hook has not run yet) but the DI scope is
                    // already gone. Expected during shutdown, and terminal — the scope never
                    // comes back, so re-arming would be a 1 Hz storm against a dead hub.
                    logger?.LogWarning(ex,
                        "[{Context}] re-establish for {ThreadPath} hit a disposed scope — " +
                        "the hub is tearing down; watcher stops (no re-arm)",
                        context, threadPath);
                }
                catch (Exception ex)
                {
                    // NOT a swallow: an unexpected synchronous fault out of Establish is
                    // surfaced at Error with full context. It is contained rather than
                    // rethrown because rethrowing here is an UNHANDLED exception on a
                    // thread-pool thread, which kills the process — the loudest possible
                    // failure and the least useful one.
                    logger?.LogError(ex,
                        "[{Context}] re-establish for {ThreadPath} threw synchronously — " +
                        "watcher stops; the thread is no longer observed",
                        context, threadPath);
                }
            });
    }
}
