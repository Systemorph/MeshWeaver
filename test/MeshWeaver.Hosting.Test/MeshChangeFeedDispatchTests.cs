using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Regression tests for issue #899 — the mesh change feed must fan out on its OWN
/// serial dispatch loop, never on the publishing hub's thread.
///
/// <para>Why this matters (the wedge these tests pin): a publisher is always some
/// hub's action block, and it calls <c>Publish</c> from inside a <c>SelectMany</c>
/// continuation that is itself running INSIDE another Rx operator's gate — in the
/// real failure, the whole <c>HandleDeleteNodeRequest</c> pipeline executed inside
/// the <c>Observable.CombineLatest</c> lock of <c>PermissionEvaluator</c>'s
/// effective-permission fold. When the fan-out ran synchronously, it acquired the
/// SHARED synced-query <c>Merge</c> gate and then walked into ANOTHER hub's fold —
/// while that hub was doing the mirror image. The two hubs took
/// {own fold gate, shared merge gate} in opposite orders and deadlocked: both
/// action blocks parked forever and the recursive delete could neither succeed nor
/// fail (no <c>DeleteNodeResponse</c> ever posted).</para>
/// </summary>
public class MeshChangeFeedDispatchTests
{
    /// <summary>
    /// The invariant: <see cref="InProcessMeshChangeFeed.Publish"/> hands the event to the
    /// dispatch loop and returns — the subscriber runs on a DIFFERENT thread. Everything
    /// else in this file follows from this one property.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Publish_runs_subscribers_off_the_publisher_thread()
    {
        using var feed = new InProcessMeshChangeFeed();

        var seen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = feed.Subscribe(_ => seen.TrySetResult(Environment.CurrentManagedThreadId));

        var publisherThread = Environment.CurrentManagedThreadId;
        feed.Publish(MeshChangeEvent.Deleted("space/Overview"));

        var handlerThread = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handlerThread.Should().NotBe(publisherThread,
            "a subscriber that runs on the publisher's thread runs inside whatever locks the "
            + "publishing hub already holds — that is the #899 lock-order inversion");
    }

    /// <summary>
    /// Ordering is part of the contract: one dispatch loop, FIFO. A feed that fanned out
    /// on the thread pool could reorder Created/Deleted for the same path.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Events_are_delivered_in_publish_order()
    {
        using var feed = new InProcessMeshChangeFeed();

        const int count = 200;
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = feed.Subscribe(e =>
        {
            received.Enqueue(e.Path);
            if (received.Count == count)
                done.TrySetResult();
        });

        var expected = new string[count];
        for (var i = 0; i < count; i++)
        {
            expected[i] = $"space/node{i}";
            feed.Publish(MeshChangeEvent.Deleted(expected[i]));
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        received.ToArray().Should().Equal(expected);
    }

    /// <summary>
    /// The actual #899 deadlock, deterministically. Two publishers each hold their OWN
    /// gate (the per-call permission fold) while publishing; the subscriber chain walks
    /// through a SHARED gate (the cached synced query both folds sit behind) into the
    /// OTHER publisher's gate. With a synchronous fan-out that is a guaranteed
    /// lock-order inversion — this test hangs and fails on the pre-fix feed. With the
    /// dispatch loop, both publishes return immediately and the loop takes the gates
    /// serially, so no cycle can form.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Concurrent_publishers_holding_their_own_gates_cannot_deadlock()
    {
        using var feed = new InProcessMeshChangeFeed();

        var ownGateOfPublisher1 = new object();   // publisher 1's CombineLatest fold gate
        var ownGateOfPublisher2 = new object();   // publisher 2's CombineLatest fold gate
        var sharedQueryGate = new object();       // the shared synced-query Merge gate

        // The subscriber graph: every event walks the shared query gate and, from there,
        // into the OTHER publisher's fold — exactly the shape the real graph has, because
        // both folds subscribe to the same cached query.
        using var subscription = feed.Subscribe(e =>
        {
            var otherFoldGate = e.Path == "p1" ? ownGateOfPublisher2 : ownGateOfPublisher1;
            lock (sharedQueryGate)
                lock (otherFoldGate) { }
        });

        // Both publishers must be inside their own gate at the same time — otherwise the
        // inversion cannot form and the test would pass vacuously.
        using var bothInsideTheirGate = new Barrier(2);

        Task PublishHolding(object ownGate, string path) => Task.Run(() =>
        {
            lock (ownGate)
            {
                bothInsideTheirGate.SignalAndWait(TimeSpan.FromSeconds(10));
                feed.Publish(MeshChangeEvent.Deleted(path));
            }
        });

        var publishers = new[]
        {
            PublishHolding(ownGateOfPublisher1, "p1"),
            PublishHolding(ownGateOfPublisher2, "p2")
        };

        // Task.Delay is the DEADLOCK bound here, not a wait-for-propagation sleep: the
        // only way both publishers fail to finish is a genuine cycle.
        var bothPublished = Task.WhenAll(publishers);
        var finished = await Task.WhenAny(bothPublished, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(bothPublished,
            "Publish must not run the subscriber graph on the publisher's thread — doing so "
            + "takes foreign gates while the publisher still holds its own, which is the "
            + "#899 deadlock between two concurrent per-node-hub deletes");
    }
}
