using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #4530 — the consequence of a pooled leg that terminates INSIDE its own subscribe call.
///
/// <para><see cref="OrderedRouteDispatcher.DrainNext"/> subscribes the next leg for a destination
/// from the previous leg's terminal notification, and says why that is safe: <i>"No recursion depth
/// to worry about: every leg is subscribed through the pool, which hops to a thread-pool thread, so
/// a leg can never complete inside its own subscribe call."</i> That premise fails the moment the
/// pool has drained. <c>SubscribeThroughPool</c> then refuses the leg at BUILD time with
/// <c>Cancelled&lt;T&gt;()</c> — <c>Observable.Throw</c> on the immediate scheduler — so the terminal is
/// delivered synchronously, on the thread that subscribed, and the leg's <c>.Finally</c> re-enters
/// <c>DrainNext</c> while that subscribe is still on the stack.</para>
///
/// <para>The queue is then walked by RECURSION, one nested frame group per queued leg, on whichever
/// thread ended the head leg — at teardown that is the pool's single <c>IoPool-cancel</c> thread,
/// inside <c>_poolCts.Cancel()</c>. The stack grows with the destination's backlog, and a backlog is
/// exactly what a saturated destination has when a silo goes down (<c>RoutingGrain.ReportSaturation</c>
/// reports it at Critical). This test measures the stack, because "it recurses" is otherwise only a
/// reading of the code.</para>
///
/// <para>The shape is the real one: a head leg in flight, a queue behind it, and a pool drain as the
/// only thing that ends the head. Nothing is timed, and nothing is swept — the stack depth at each
/// leg's completion is recorded and compared.</para>
/// </summary>
public class OrderedRouteDispatcherDrainRecursionTest
{
    private const string Destination = "client/subscriber-1";

    /// <summary>Legs queued BEHIND the in-flight head leg. Enough that a per-leg stack frame group is
    /// unmistakable next to the frame count of a single completion, and small enough that the
    /// recursion this pins cannot overflow the stack and kill the host.</summary>
    private const int QueuedBehindHead = 32;

    [Fact]
    public async Task ADrainedQueue_CompletesItsLegs_WithoutRecursingOncePerLeg()
    {
        using var pool = new IoPool(4);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);

        var headSubscribed = new AsyncSubject<Unit>();
        var allCompleted = new AsyncSubject<Unit>();
        var completedCount = 0;
        // The stack depth at each leg's completion callback — the quantity this test exists to read.
        var depthAtCompletion = new int[QueuedBehindHead + 1];
        var stackAtDeepest = string.Empty;

        void Record(int leg)
        {
            var stack = new StackTrace(fNeedFileInfo: false);
            depthAtCompletion[leg] = stack.FrameCount;
            if (leg == QueuedBehindHead)
                stackAtDeepest = DrainNextFrames(stack);
            if (Interlocked.Increment(ref completedCount) == QueuedBehindHead + 1)
            {
                allCompleted.OnNext(Unit.Default);
                allCompleted.OnCompleted();
            }
        }

        // The head leg is a LIVE subscription that ends only when the pool drains — a route leg
        // whose post is in flight when the silo goes down.
        dispatcher.Enqueue(
            Destination,
            Observable.Create<Unit>(_ =>
            {
                headSubscribed.OnNext(Unit.Default);
                headSubscribed.OnCompleted();
                return Disposable.Empty;
            }),
            () => Record(0));

        await headSubscribed.Should().Within(TestTimeouts.Quick).Emit(
            "precondition: the head leg holds the destination, so everything enqueued now QUEUES "
            + "behind it instead of being dispatched",
            cancellationToken: TestContext.Current.CancellationToken);

        for (var leg = 1; leg <= QueuedBehindHead; leg++)
        {
            var queued = leg;
            dispatcher.Enqueue(Destination, Observable.Never<Unit>(), () => Record(queued));
        }

        dispatcher.QueueSnapshot().Deepest.Should().Be(QueuedBehindHead,
            "precondition: the whole backlog is queued behind the head leg before anything drains");

        // Teardown. The head leg's terminal comes from the pool's drain registration; every leg
        // behind it is then BUILT after the drain, so each one is refused.
        pool.Drain();

        await allCompleted.Should().Within(TestTimeouts.Quick).Emit(
            "every queued leg must still terminate — its .Finally is what releases RoutingGrain's "
            + "in-flight route slot and advances the destination's FIFO (#1789)",
            cancellationToken: TestContext.Current.CancellationToken);

        // Leg 0 ends on the canceller thread and the rest are refusals, so only the refusals are
        // compared with each other: they all travel the identical path, and the ONLY thing that can
        // make their stacks differ is one of them running inside another's subscribe.
        var refused = depthAtCompletion.Skip(1).ToArray();
        var spread = refused.Max() - refused.Min();

        spread.Should().BeLessThan(QueuedBehindHead,
            "a refused leg must not complete inside the subscribe of the leg ahead of it: DrainNext "
            + "re-enters itself from each terminal, so an inline refusal walks the whole backlog by "
            + "RECURSION — one frame group per queued leg, on the pool's single cancel thread. "
            + "Depths {0}..{1} over {2} legs; DrainNext frames under the last completion: [{3}]",
            refused.Min(), refused.Max(), refused.Length, stackAtDeepest);
    }

    /// <summary>
    /// How many <c>DrainNext</c> frames (the method and the <c>.Finally</c> lambda it hangs off) are
    /// on the stack — the recursion depth in the form that names itself, for the failure message.
    /// Diagnostic only: the assertion above is on the frame COUNT, which no compiler naming
    /// convention can change.
    /// </summary>
    private static string DrainNextFrames(StackTrace stack) =>
        stack.GetFrames()
            .Select(frame => frame.GetMethod()?.Name ?? string.Empty)
            .Count(name => name.Contains(nameof(OrderedRouteDispatcher.Enqueue), StringComparison.Ordinal)
                || name.Contains("DrainNext", StringComparison.Ordinal))
            .ToString();
}
