using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// What was left behind when mesh teardown reached its terminal point. <see cref="Clean"/> is the
/// contract: every action block drained, every pooled I/O leaf joined, every async cleanup ran.
/// A non-clean report means live work survived teardown — the precondition of the
/// use-after-unload SIGSEGV (a ThreadPool thread dereferencing a collectible node ALC's freed
/// metadata after the scope disposed and unloaded it). Consumers must SURFACE a dirty report
/// (fail the test class, error-log the host shutdown), never swallow it: a drain that silently
/// gives up is how "disposal completed" becomes a lie and the crash moves a few milliseconds
/// past teardown where nothing can attribute it.
/// </summary>
/// <param name="LeakedIoLeaves">Pooled I/O leaves that ignored cancellation and were still
/// running when their drain budget expired (see <c>IoPool.Drain</c>). 0 = the join is real.</param>
/// <param name="AsyncDisposeClean">Whether every cleanup on the <see cref="AsyncDisposeQueue"/>
/// ran (or unwound after cancellation) within its quiesce budget.</param>
public sealed record TeardownReport(int LeakedIoLeaves, bool AsyncDisposeClean)
{
    /// <summary>
    /// WHICH pools did not finish, and by how much — empty on a clean drain.
    ///
    /// <para>🚨 <see cref="LeakedIoLeaves"/> alone is a bare count, and a bare count is not
    /// actionable: <c>Query=1</c> and <c>Compile=1</c> are different bugs with different owners.
    /// The registry already logs the name, but <c>DrainAll</c> runs after the mesh's log sink has
    /// stopped capturing, so that warning cannot be read in the window it describes (#2616).
    /// Carrying it on the report puts it somewhere a subscriber can still see.</para>
    /// </summary>
    public IReadOnlyList<IoPoolRegistry.PoolResidual> ResidualByPool { get; init; } = [];

    /// <summary>
    /// The exception the hub's <c>DisposalCompleted</c> reported instead of a completion, or
    /// <c>null</c> when disposal finished normally.
    ///
    /// <para>🚨 This used to be DISCARDED. The wait was
    /// <c>DisposalCompleted.Catch(_ =&gt; Observable.Return(Unit.Default))</c>, which turns a
    /// faulted disposal into an indistinguishable success — issue #2488 lists it as one of the
    /// swallow-and-continue sites. It is now OBSERVED and carried here, and logged at Error by the
    /// orchestrator. It deliberately does NOT (yet) affect <see cref="Clean"/>: whether a faulted
    /// disposal should FAIL a test class or a host shutdown is an escalation decision of its own,
    /// separate from the observation defect this record's population fixes.</para>
    /// </summary>
    public Exception? DisposalFault { get; init; }

    /// <summary>
    /// Whether the pre-dispose activity quiesce reached idle within its budget. <c>false</c> means
    /// a run was still writing when teardown proceeded — previously indistinguishable from idle,
    /// because the timeout was folded into a successful completion (#2488, site 2).
    /// </summary>
    public bool ActivitiesQuiesced { get; init; } = true;

    /// <summary>
    /// Activities the pre-dispose quiesce had to CANCEL because they stopped reporting progress
    /// (see <c>ActivityTracker.Quiesce</c>). Each is a run that did not finish its job — the
    /// teardown killed it — and is logged at Error by the orchestrator. Empty when every run
    /// finished on its own.
    /// </summary>
    public IReadOnlyList<string> CancelledActivities { get; init; } = [];

    /// <summary>
    /// Activities that ignored the cancellation the quiesce handed them and were left behind so
    /// teardown could proceed. A run listed here is a defect in that run: it observes neither
    /// progress nor cancellation.
    /// </summary>
    public IReadOnlyList<string> AbandonedActivities { get; init; } = [];

    /// <summary>
    /// Pooled I/O leaves the drain had to CANCEL because they outlived the drain grace with their
    /// pool making no further progress (<c>IoPool.LeavesCancelledAfterGrace</c>). They unwound —
    /// they are not in <see cref="LeakedIoLeaves"/> — but each is a unit of work that did not
    /// finish, and the drain names it per pool in <see cref="CancelledIoByPool"/>.
    /// </summary>
    public int CancelledIoLeaves { get; init; }

    /// <summary>Per-pool detail for <see cref="CancelledIoLeaves"/>; empty when nothing was cancelled.</summary>
    public IReadOnlyList<IoPoolRegistry.PoolResidual> CancelledIoByPool { get; init; } = [];

    /// <summary>
    /// True when the teardown had to KILL something to get here — an activity cancelled or
    /// abandoned, a pooled leaf cancelled after the grace. The scope may still be safe to dispose
    /// (<see cref="Clean"/> answers that); this answers whether every unit of work finished its
    /// job, which is the contract teardown is held to. A consumer surfaces it like a dirty report.
    /// </summary>
    public bool WorkWasKilled =>
        CancelledActivities.Count > 0 || AbandonedActivities.Count > 0 || CancelledIoLeaves > 0;

    /// <summary>True iff nothing survived teardown — the scope may be disposed and node ALCs
    /// unloaded with no thread still executing their code.</summary>
    public bool Clean => LeakedIoLeaves == 0 && AsyncDisposeClean;

    /// <summary>One-line summary for logs and failure messages.</summary>
    public override string ToString()
    {
        var notes = string.Empty;
        if (!ActivitiesQuiesced)
            notes += "; activities did NOT quiesce within budget";
        if (CancelledActivities.Count > 0)
            notes += $"; {CancelledActivities.Count} activit{(CancelledActivities.Count == 1 ? "y" : "ies")} CANCELLED for making no progress [{string.Join(" | ", CancelledActivities)}]";
        if (AbandonedActivities.Count > 0)
            notes += $"; {AbandonedActivities.Count} activit{(AbandonedActivities.Count == 1 ? "y" : "ies")} ABANDONED after ignoring cancellation [{string.Join(" | ", AbandonedActivities)}]";
        if (CancelledIoLeaves > 0)
            notes += $"; {CancelledIoLeaves} pooled I/O leaf(s) CANCELLED after the drain grace [{string.Join(", ", CancelledIoByPool)}]";
        if (DisposalFault is not null)
            notes += $"; disposal FAULTED: {DisposalFault.GetType().Name}: {DisposalFault.Message}";
        return (Clean
            ? "teardown clean — all pooled I/O joined, async dispose queue drained"
            : $"teardown DIRTY — {LeakedIoLeaves} pooled I/O leaf(s) still running, "
              + $"async dispose queue {(AsyncDisposeClean ? "drained" : "still running")}") + notes;
    }
}

/// <summary>
/// The mesh's terminal "all is done" signal — the very END of teardown, strictly after
/// <see cref="Messaging.IMessageHub.DisposalCompleted"/> (which only covers the action blocks and
/// message round-trips): it also accounts for the offloaded <c>IIoPool</c> ThreadPool work and the
/// <see cref="AsyncDisposeQueue"/>. Completed exactly once by the teardown orchestrator
/// (<c>MeshTeardownExtensions</c>); everything that must not run before teardown truly ends —
/// disposing the service scope, unloading node ALCs, starting the next test's mesh — subscribes
/// here rather than re-deriving "done" from partial signals.
///
/// <para>Mesh-scoped singleton (never static — dies with the mesh, NoStaticState). Backed by a
/// <see cref="ReplaySubject{T}"/>(1) exactly like <c>MessageHub.disposalCompleted</c>: a
/// subscriber that attaches after teardown already finished still receives the report
/// immediately. Signalling is idempotent via CAS — first report wins.</para>
/// </summary>
public sealed class MeshTeardownSignal
{
    private readonly ReplaySubject<TeardownReport> completed = new(1);
    private int signalled;

    /// <summary>
    /// Fires the final <see cref="TeardownReport"/> once, then completes — the observable
    /// "notification at the very end that all is done". Never errors: a dirty teardown is DATA
    /// (the report), surfaced by the consumer, not an Rx fault that would tear subscribers down.
    /// </summary>
    public IObservable<TeardownReport> Completed => completed.AsObservable();

    /// <summary>Completes <see cref="Completed"/> exactly once (idempotent CAS) — called only by
    /// the teardown orchestrator when every drain phase has finished.</summary>
    public void SignalCompleted(TeardownReport report)
    {
        if (Interlocked.CompareExchange(ref signalled, 1, 0) != 0)
            return;
        completed.OnNext(report);
        completed.OnCompleted();
    }
}
