using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The reusable probe for the issue-#899 bug CLASS: an Rx fan-out point that walks its
/// subscriber graph on the PUBLISHER'S thread lets a publisher acquire foreign gates while
/// still holding its own, and two publishers doing that concurrently deadlock.
///
/// <para>Point it at any fan-out point (mesh change feed, a storage adapter's
/// <c>Changes</c>, a permission fold's continuation) by supplying how to subscribe and how to
/// publish. The harness recreates the exact shape of the real graph:</para>
///
/// <list type="number">
///   <item>each publisher holds its OWN gate — the per-call
///   <c>Observable.CombineLatest</c> lock of <c>PermissionEvaluator</c>'s effective-permission
///   fold, which is held for the whole handler body because the fold emits synchronously
///   during <c>Subscribe</c>;</item>
///   <item>a <see cref="Barrier"/> guarantees BOTH are inside their own gate at the same
///   moment — without it the inversion cannot form and the test would pass vacuously;</item>
///   <item>the subscriber chain walks a SHARED gate (<c>PersistenceService.Changes</c>'s
///   <c>Merge</c>, the process-wide <c>Replay(1)</c> in <c>IMeshNodeStreamCache</c>) and from
///   there into the OTHER publisher's fold — because both folds subscribe the same cached
///   query.</item>
/// </list>
///
/// <para>With a synchronous fan-out that is a guaranteed lock-order inversion and the probe
/// hangs. With enqueue-and-dispatch both publishes return immediately and the dispatch chain
/// takes the gates serially, so no cycle can form.</para>
/// </summary>
internal static class RxFanOutInversionHarness
{
    /// <summary>Bound on the whole probe. A genuine cycle is the only way to exceed it.</summary>
    private static readonly TimeSpan DeadlockBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asserts that two publishers, each holding its own gate, cannot deadlock through
    /// <paramref name="publish"/>.
    /// </summary>
    /// <param name="subscribe">
    /// Attaches a handler to the fan-out point under test. The handler receives the tag the
    /// publisher passed, so it can walk into the OTHER publisher's gate.
    /// </param>
    /// <param name="publish">Publishes one event carrying the given tag.</param>
    /// <param name="because">What the assertion proves, for the failure message.</param>
    public static async Task AssertCannotDeadlock(
        Func<Action<string>, IDisposable> subscribe,
        Action<string> publish,
        string because)
    {
        const string tag1 = "p1";
        const string tag2 = "p2";

        var ownGateOfPublisher1 = new object();   // publisher 1's CombineLatest fold gate
        var ownGateOfPublisher2 = new object();   // publisher 2's CombineLatest fold gate
        var sharedQueryGate = new object();       // the shared synced-query / Merge gate

        using var subscription = subscribe(tag =>
        {
            var otherFoldGate = tag == tag1 ? ownGateOfPublisher2 : ownGateOfPublisher1;
            lock (sharedQueryGate)
                lock (otherFoldGate) { }
        });

        using var bothInsideTheirGate = new Barrier(2);

        Task PublishHolding(object ownGate, string tag) => Task.Run(() =>
        {
            lock (ownGate)
            {
                bothInsideTheirGate.SignalAndWait(DeadlockBound);
                publish(tag);
            }
        });

        var bothPublished = Task.WhenAll(
            PublishHolding(ownGateOfPublisher1, tag1),
            PublishHolding(ownGateOfPublisher2, tag2));

        // Task.Delay is the DEADLOCK BOUND here, not a wait-for-propagation sleep: the only
        // way both publishers fail to finish is a genuine cycle.
        var finished = await Task.WhenAny(bothPublished, Task.Delay(DeadlockBound));

        finished.Should().BeSameAs(bothPublished, because);
    }
}
