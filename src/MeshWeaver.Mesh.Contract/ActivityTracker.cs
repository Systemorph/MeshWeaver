using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;

namespace MeshWeaver.Mesh;

/// <summary>
/// Counts the activity runs currently in flight on this mesh, so teardown can QUIESCE — stop
/// starting new work, let what is running finish, and only then tear down — and so it can tell a
/// run that is still WORKING from one that has WEDGED.
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
/// <para><b>Quiesce; cancel only what has stopped.</b> A run that is making progress is allowed to
/// finish and emit its terminal ActivityLog status, however long that takes within the caller's
/// budget; teardown waits for it. A run that has made NO progress for a whole stall budget is
/// wedged: it is handed its cancellation (the one cooperative kill an activity has — the same
/// token a user's cancel request trips), reported by name, and if it ignores that too it is
/// reported again as abandoned so teardown can proceed without pretending the run finished.
/// "Progress" is what the run itself reports through <see cref="ActivityRunHandle.Progress"/> —
/// every appended log line, every status write — never a heartbeat the tracker invents.</para>
///
/// <para><b>Must not deadlock.</b> The wait belongs to the teardown CALLER, never to a hub action
/// block: a running activity's own Append/Finish writes go back through the hubs, so blocking a hub
/// turn on this would block the very work it waits for. It is also bounded by the caller — a run
/// that never settles must surface as a timeout, not hang teardown forever.</para>
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
    // The live runs, keyed by ticket, so a quiesce can look at EACH one's progress rather than at
    // an anonymous count. Instance field on the mesh-scoped singleton — never static.
    private readonly ConcurrentDictionary<long, ActivityRunHandle> runs = new();
    private long ticketSeq;

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

    /// <summary>The runs currently in flight — a snapshot, most recently started last.</summary>
    public IReadOnlyList<ActivityRunHandle> InFlight =>
        runs.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToImmutableList();

    /// <summary>
    /// Registers one run as in flight. Dispose the returned handle when the run reaches a TERMINAL
    /// state (succeeded or failed) — not when it was merely dispatched. A double dispose does not
    /// double-decrement.
    /// </summary>
    public IDisposable Track() => TrackRun("(unlabelled run)", cancel: null);

    /// <summary>
    /// <see cref="Track"/>, with the two things a quiesce needs to treat the run as WORK rather
    /// than as a count: a <paramref name="label"/> to name it in a report, and the
    /// <paramref name="cancel"/> that is its cooperative kill (trip the same token a user's
    /// cancel request would). The run reports its own progress through
    /// <see cref="ActivityRunHandle.Progress"/>; a run that never does is treated as stalled from
    /// the moment the quiesce begins looking.
    /// </summary>
    /// <param name="label">Names the run — its activity path, typically.</param>
    /// <param name="cancel">Requests the run stop; <c>null</c> when it has no cancellation.</param>
    /// <returns>The handle; dispose it when the run is terminal.</returns>
    public ActivityRunHandle TrackRun(string label, Action? cancel)
    {
        var handle = new ActivityRunHandle(this, Interlocked.Increment(ref ticketSeq), label, cancel);
        runs[handle.Ticket] = handle;
        deltas.OnNext(1);
        return handle;
    }

    internal void Complete(ActivityRunHandle handle)
    {
        if (runs.TryRemove(handle.Ticket, out _))
            deltas.OnNext(-1);
    }

    /// <summary>
    /// Waits for the in-flight runs to finish, cancelling any that stop making progress, and
    /// reports what it had to do. Completes with the report when every run has either finished or
    /// been abandoned — never faults. Emits at once on an idle mesh.
    ///
    /// <para>The <paramref name="stallBudget"/> is a STALL bound, not a duration: a run that keeps
    /// reporting progress is waited for indefinitely (the caller bounds the whole wait with its own
    /// token). A run with no progress for one budget is cancelled and listed in
    /// <see cref="ActivityQuiesceReport.Cancelled"/>; one that then shows no progress for another
    /// budget is listed in <see cref="ActivityQuiesceReport.Abandoned"/> and no longer holds the
    /// quiesce open — it ignored its cancellation, and that is a defect in the run.</para>
    /// </summary>
    /// <param name="stallBudget">How long a run may go without reporting progress before it is
    /// treated as wedged.</param>
    /// <returns>A single-emission observable carrying the report.</returns>
    public IObservable<ActivityQuiesceReport> Quiesce(TimeSpan stallBudget)
    {
        if (stallBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stallBudget));

        return Observable.Create<ActivityQuiesceReport>(observer =>
        {
            var cancelled = ImmutableList<string>.Empty;
            var abandoned = ImmutableList<string>.Empty;
            var done = 0;

            void Finish()
            {
                if (Interlocked.Exchange(ref done, 1) != 0)
                    return;
                observer.OnNext(new ActivityQuiesceReport(cancelled, abandoned));
                observer.OnCompleted();
            }

            // Every verdict runs on the tracker's own event loop, so the two lists above are only
            // ever touched from one thread and the poll cannot overlap itself.
            // One-shot: a fault on the count stream ends the quiesce with what it has — the
            // report is still the truthful answer, and a fault here must never leave the
            // teardown waiting on a signal that will not come.
            var idle = counts.Where(running => running == 0).Take(1)
                .ObserveOn(scheduler)
                .Subscribe(_ => Finish(), _ => Finish());

            var period = TimeSpan.FromMilliseconds(Math.Max(20, stallBudget.TotalMilliseconds / 4));
            // Long-lived: a fault INSIDE a verdict is handled in onNext (the quiesce ends with the
            // report it has rather than stopping silently), and the onError arm covers the
            // interval itself faulting.
            var poll = Observable.Interval(period, scheduler).Subscribe(_ =>
            {
                try
                {
                    if (Volatile.Read(ref done) != 0)
                        return;
                    var live = runs.Values.ToArray();
                    if (live.Length == 0)
                    {
                        Finish();
                        return;
                    }
                    var holding = 0;
                    foreach (var run in live)
                    {
                        if (run.Abandoned)
                            continue;
                        if (!run.CancelRequested)
                        {
                            if (run.SinceLastProgress < stallBudget)
                            {
                                holding++;
                                continue;
                            }
                            run.RequestCancel();
                            cancelled = cancelled.Add(run.Label);
                            holding++;
                            continue;
                        }
                        if (run.SinceCancelRequested < stallBudget)
                        {
                            holding++;
                            continue;
                        }
                        run.MarkAbandoned();
                        abandoned = abandoned.Add(run.Label);
                    }
                    if (holding == 0)
                        Finish();
                }
                catch
                {
                    Finish();
                }
            }, _ => Finish());

            return new CompositeDisposable(idle, poll);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        connection.Dispose();
        deltas.Dispose();
        scheduler.Dispose();
    }
}

/// <summary>
/// One in-flight activity run as the tracker sees it: what it is called, when it last reported
/// progress, and how to ask it to stop. Handed out by <see cref="ActivityTracker.TrackRun"/>;
/// disposed by the run when it reaches a terminal state.
/// </summary>
public sealed class ActivityRunHandle : IDisposable
{
    private readonly ActivityTracker owner;
    private readonly Action? cancel;
    private long lastProgressTicks;
    private long cancelRequestedTicks;
    private int cancelRequested;
    private int abandoned;
    private int disposed;

    internal ActivityRunHandle(ActivityTracker owner, long ticket, string label, Action? cancel)
    {
        this.owner = owner;
        this.cancel = cancel;
        Ticket = ticket;
        Label = label;
        lastProgressTicks = Stopwatch.GetTimestamp();
    }

    internal long Ticket { get; }

    /// <summary>Names the run — its activity path, typically.</summary>
    public string Label { get; }

    /// <summary>How long since the run last reported <see cref="Progress"/> (or started).</summary>
    public TimeSpan SinceLastProgress =>
        Stopwatch.GetElapsedTime(Volatile.Read(ref lastProgressTicks));

    /// <summary>Whether a quiesce has asked this run to stop.</summary>
    public bool CancelRequested => Volatile.Read(ref cancelRequested) != 0;

    /// <summary>Whether a quiesce gave up waiting on this run after it ignored its cancellation.</summary>
    public bool Abandoned => Volatile.Read(ref abandoned) != 0;

    internal TimeSpan SinceCancelRequested =>
        Stopwatch.GetElapsedTime(Volatile.Read(ref cancelRequestedTicks));

    /// <summary>
    /// The run reports that it is still working. Call it from every observable step — an appended
    /// log line, a status write, a completed sub-operation. A quiesce treats the time since the
    /// last call as the run's stall.
    /// </summary>
    public void Progress() => Volatile.Write(ref lastProgressTicks, Stopwatch.GetTimestamp());

    internal void RequestCancel()
    {
        if (Interlocked.Exchange(ref cancelRequested, 1) != 0)
            return;
        Volatile.Write(ref cancelRequestedTicks, Stopwatch.GetTimestamp());
        try { cancel?.Invoke(); }
        catch
        {
            // A cancel that throws is the run's problem to report; the quiesce still counts the
            // request as made and moves on to the abandon verdict on the next budget.
        }
    }

    internal void MarkAbandoned() => Interlocked.Exchange(ref abandoned, 1);

    /// <summary>Marks the run terminal. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        owner.Complete(this);
    }
}

/// <summary>
/// What an <see cref="ActivityTracker.Quiesce"/> had to do to reach idle. Empty lists mean every
/// run finished on its own; a name in <see cref="Cancelled"/> is a run that stopped making progress
/// and was told to stop; a name in <see cref="Abandoned"/> is one that ignored that too.
/// </summary>
/// <param name="Cancelled">Runs the quiesce cancelled for making no progress.</param>
/// <param name="Abandoned">Runs that ignored their cancellation and were left behind.</param>
public sealed record ActivityQuiesceReport(IReadOnlyList<string> Cancelled, IReadOnlyList<string> Abandoned)
{
    /// <summary>True when no run had to be cancelled or abandoned.</summary>
    public bool Clean => Cancelled.Count == 0 && Abandoned.Count == 0;

    /// <inheritdoc />
    public override string ToString() =>
        Clean
            ? "every activity finished on its own"
            : $"cancelled {Cancelled.Count} stalled activit{(Cancelled.Count == 1 ? "y" : "ies")}"
              + (Cancelled.Count == 0 ? string.Empty : $" [{string.Join(" | ", Cancelled)}]")
              + $", abandoned {Abandoned.Count}"
              + (Abandoned.Count == 0 ? string.Empty : $" [{string.Join(" | ", Abandoned)}]");
}
