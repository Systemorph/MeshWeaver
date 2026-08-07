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
    /// The actual #899 deadlock, deterministically — driven by the shared
    /// <see cref="RxFanOutInversionHarness"/> so the SAME probe pins every fan-out point in
    /// the mesh (see <c>StorageAdapterDispatchTests</c>, <c>PermissionFoldGateTests</c>).
    /// With a synchronous fan-out this hangs and fails; with enqueue-and-dispatch both
    /// publishes return immediately and the dispatch chain takes the gates serially.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Concurrent_publishers_holding_their_own_gates_cannot_deadlock()
    {
        using var feed = new InProcessMeshChangeFeed();

        await RxFanOutInversionHarness.AssertCannotDeadlock(
            subscribe: handler => feed.Subscribe(e => handler(e.Path)),
            publish: tag => feed.Publish(MeshChangeEvent.Deleted(tag)),
            because:
                "Publish must not run the subscriber graph on the publisher's thread — doing so "
                + "takes foreign gates while the publisher still holds its own, which is the "
                + "#899 deadlock between two concurrent per-node-hub deletes");
    }

    /// <summary>
    /// A subscriber that throws must not starve the OTHERS (the #889 half of the contract,
    /// now inherited from <c>DispatchedChangeFeed</c>). The pre-#899 feed delivered through a
    /// plain <c>Subject</c>, where the first throwing observer aborted delivery to every
    /// observer subscribed after it — and the publisher's <c>catch</c> turned that into
    /// silence.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task A_throwing_subscriber_does_not_starve_the_others()
    {
        using var feed = new InProcessMeshChangeFeed();

        using var faulty = feed.Subscribe(_ => throw new InvalidOperationException("boom"));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var healthy = feed.Subscribe(_ => reached.TrySetResult());

        feed.Publish(MeshChangeEvent.Deleted("space/Overview"));

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
