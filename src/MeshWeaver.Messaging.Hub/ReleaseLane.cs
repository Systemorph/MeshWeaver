using System.Reactive.Concurrency;
using System.Runtime.ExceptionServices;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Messaging;

/// <summary>
/// The ONE ordered lane on which released owned connections tell their attached subscribers that
/// they are over (<see cref="OwnedConnectionExtensions"/>). Releases posted here run on the
/// ThreadPool — never on the thread that disposed the owner — and ONE AT A TIME, in the order they
/// were posted.
///
/// <para>🚨 <b>Why one at a time — the deadlock this exists to prevent.</b> An owner registry is
/// released in ONE synchronous sweep (<c>MeshNodeStreamCache</c> disposes every query connection it
/// holds; a hub's ShutDown disposes every registration), and each release terminates the subscribers
/// still attached to its connection. Delivered as one independent ThreadPool work item per
/// connection, a sweep of N connections produced N terminals CONCURRENTLY — and a consumer that
/// composes several of those connections received them on N threads at once. Rx combinators take
/// their gate on a terminal and dispose their other sources under it: <c>CombineLatest</c> forwards
/// an error and disposes its upstream subscriptions while holding its gate, <c>Zip</c> forwards an
/// error while holding ITS gate. The permission fold nests a <c>Zip</c> under a
/// <c>CombineLatest</c>, so one release entered the outer gate and waited for the inner while
/// another entered the inner gate and waited for the outer: a lock-order deadlock between two
/// ThreadPool threads that never resolves, and the disposing thread then parked on the same gate
/// (<c>UiContributionCatalog.Dispose</c> completing its subject into that
/// <c>CombineLatest</c>). Measured in MeshWeaver.Plugins CI on set 3.0.0-ci.9296 as a test
/// "timed out" inside teardown after its mesh had disposed cleanly, and reproduced under the MTP
/// runner with a stack capture of exactly those three threads. Delivered on one lane, the second
/// terminal reaches a consumer the first has already torn down — nothing left to contend.</para>
///
/// <para><b>Which lane.</b> Every hub resolves the mesh's lane (registered on the root hub, the way
/// <see cref="AccessService"/> is), so every hub-owned connection in a mesh releases on it; a
/// registry that owns connections outside a hub holds one lane for all of them. Two connections a
/// consumer may compose must release on the same lane — that is the whole guarantee. Never a
/// static: a lane lives exactly as long as the mesh (or registry) that owns it.</para>
///
/// <para><b>Not a gate.</b> Nothing waits on the lane; a post is an enqueue. The serialisation is
/// Rx's own <c>ObserveOn</c> queue, fed through <see cref="Subject.Synchronize{TSource}(ISubject{TSource})"/>
/// so posts from several disposing threads keep the Rx grammar.</para>
/// </summary>
public sealed class ReleaseLane
{
    private readonly ISubject<Action> releases;

    /// <summary>Creates a lane whose releases run serially on the ThreadPool.</summary>
    public ReleaseLane()
    {
        var subject = new Subject<Action>();
        releases = Subject.Synchronize(subject);
        // Never disposed on purpose: the lane is its owner's for the owner's whole life, and a
        // release posted during the owner's own teardown must still be delivered. Nothing roots
        // the subscription but a queued release, so the lane is collected with its owner.
        subject
            .ObserveOn(TaskPoolScheduler.Default)
            .Subscribe(Run);
    }

    /// <summary>
    /// Runs one release. A release is a subscriber's error continuation, and one that THROWS must not
    /// take the lane down with it: escaping the <c>ObserveOn</c> drain would end the lane's only
    /// observer and strand every release queued behind it — the teardown hang again, one level up.
    /// So the exception is re-raised on a ThreadPool work item of its own — exactly where it was
    /// raised when each release was its own work item, so it is as loud as it always was (an
    /// unhandled ThreadPool exception) — and the lane drains on.
    /// </summary>
    private static void Run(Action release)
    {
        try
        {
            release();
        }
        catch (Exception ex)
        {
            var captured = ExceptionDispatchInfo.Capture(ex);
            ThreadPool.UnsafeQueueUserWorkItem(static c => c.Throw(), captured, preferLocal: false);
        }
    }

    /// <summary>Queues <paramref name="release"/> behind every release posted before it.</summary>
    /// <param name="release">The terminal to deliver.</param>
    internal void Post(Action release) => releases.OnNext(release);
}
