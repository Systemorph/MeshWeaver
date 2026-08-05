using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;

namespace MeshWeaver.Mesh;

/// <summary>
/// Counts the activity runs currently in flight on this mesh, so teardown can QUIESCE — stop
/// starting new work, let what is running finish, and only then tear down.
///
/// <para><b>Why the existing drains do not cover this.</b> Mesh teardown already has three phases
/// (<c>DisposalCompleted</c> → <c>IoPoolRegistry.DrainAll()</c> → <c>AsyncDisposeQueue</c>), but an
/// activity falls through all of them:</para>
/// <list type="bullet">
///   <item><description>the trigger returns as soon as the activity exists (by design — it must not
///     block the caller), so the action-block drain has nothing outstanding to wait on;</description></item>
///   <item><description>the command runs off-turn via <c>ScheduleOffHubTurn</c>, so it holds no
///     grain turn;</description></item>
///   <item><description><c>SubscribeThroughPool</c> holds its pool permit for the SUBSCRIBE window
///     only — deliberately, to protect the Autofac <c>BeginLifetimeScope</c>. Work that continues
///     past the subscribe (a timer, an await continuation, a multi-second command) has already
///     released the permit, so <c>DrainAll()</c> joins nothing.</description></item>
/// </list>
///
/// <para>Measured before this existed: a 5-second activity, and <c>TeardownAsync</c> returned after
/// 2028 ms with the command still running. Work that outlives the mesh keeps executing against a
/// torn-down container — and when it touches a collectible ALC's metadata after unload, the process
/// dies on a bare signal with no managed exception (FutuRe.Test exit=139, ~1 run in 5).</para>
///
/// <para><b>Quiesce, do not cancel.</b> The run is allowed to finish and emit its terminal
/// ActivityLog status; teardown waits for that. Cancelling would abort work the caller believes is
/// running and would leave the activity in a non-terminal state.</para>
///
/// <para><b>Must not deadlock.</b> The wait belongs to the teardown CALLER, never to a hub action
/// block: a running activity's own Append/Finish writes go back through the hubs, so blocking a hub
/// turn on this would block the very work it waits for. It is also bounded — a run that never
/// settles must surface as a timeout, not hang teardown forever.</para>
///
/// <para>Instance state owned by the mesh (never <c>static</c>): the counter dies with the mesh
/// rather than bleeding into the next test — see <c>Doc/Architecture/NoStaticState</c>.</para>
/// </summary>
public sealed class ActivityTracker : IDisposable
{
    // Deltas in, running count out. Scan accumulates — no lock, no Interlocked, no counter field.
    //
    // 🚨 No synchronisation primitive anywhere, including Observer.Synchronize: that takes a lock,
    // and a lock reachable from hub-adjacent code is a deadlock waiting to happen. Concurrent
    // Track()/dispose calls are serialised by QUEUEING them onto a scheduler (ObserveOn), which is
    // how Rx serialises without anyone blocking. Scan therefore always runs on one thread and the
    // running total cannot interleave.
    private readonly Subject<int> deltas = new();
    private readonly EventLoopScheduler scheduler = new();
    private readonly IConnectableObservable<int> counts;
    private readonly IDisposable connection;

    /// <summary>Initializes a new instance of the <see cref="ActivityTracker"/> class.</summary>
    public ActivityTracker()
    {
        // Replay(1) so a late subscriber (teardown) sees the CURRENT count immediately rather than
        // waiting for the next change — a teardown on an idle mesh must complete at once.
        counts = deltas
            .ObserveOn(scheduler)
            .Scan(0, (running, delta) => running + delta)
            .StartWith(0)
            .Replay(1);
        connection = counts.Connect();
    }

    /// <summary>
    /// Live count of in-flight runs, starting with the current value. Emits on every start and
    /// every completion, so a consumer waits for zero without polling.
    /// </summary>
    public IObservable<int> InFlightChanges => counts;

    /// <summary>
    /// Completes once no run is in flight. Emits immediately when the mesh is already idle.
    /// </summary>
    public IObservable<Unit> WhenIdle =>
        counts.Where(running => running == 0).Take(1).Select(_ => Unit.Default);

    /// <summary>
    /// Registers one run as in flight. Dispose the returned handle when the run reaches a TERMINAL
    /// state (succeeded or failed) — not when it was merely dispatched. A double dispose does not
    /// double-decrement.
    /// </summary>
    public IDisposable Track()
    {
        deltas.OnNext(1);
        return Disposable.Create(() => deltas.OnNext(-1));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        connection.Dispose();
        deltas.Dispose();
        scheduler.Dispose();
    }
}
