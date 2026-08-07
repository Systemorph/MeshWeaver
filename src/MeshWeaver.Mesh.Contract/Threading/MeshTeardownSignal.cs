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
    /// <summary>True iff nothing survived teardown — the scope may be disposed and node ALCs
    /// unloaded with no thread still executing their code.</summary>
    public bool Clean => LeakedIoLeaves == 0 && AsyncDisposeClean;

    /// <summary>One-line summary for logs and failure messages.</summary>
    public override string ToString() => Clean
        ? "teardown clean — all pooled I/O joined, async dispose queue drained"
        : $"teardown DIRTY — {LeakedIoLeaves} pooled I/O leaf(s) still running, "
          + $"async dispose queue {(AsyncDisposeClean ? "drained" : "still running")}";
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
