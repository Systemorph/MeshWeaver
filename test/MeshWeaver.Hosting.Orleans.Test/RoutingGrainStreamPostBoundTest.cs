using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the terminal contract of the memory-stream leg of a route (issue #1028).
///
/// <para><b>Why this leg needs a terminal bound.</b> A dropped memory-stream post has NO downstream
/// response / <see cref="DeliveryFailure"/> path of its own — the stream-routed hub simply never
/// sees the message. Before #1028 the post was AWAITED inside the routing grain's turn, so a post
/// that never completed wedged the whole silo's routing AND told nobody. Now the post runs off the
/// turn, which fixes the wedge; this test pins the other half: it must always TERMINATE (so the
/// pool slot and the in-flight count are released) and the sender must always be told (so its
/// <c>Observe</c> fires OnError instead of parking forever).</para>
///
/// <para>Deterministic — <see cref="TestScheduler"/> for the timeout, no cluster, no wall clock.</para>
/// </summary>
public class RoutingGrainStreamPostBoundTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly Address Sender = new("client", "sender-1");

    [Fact]
    public void PostThatNeverCompletes_TerminatesOnTimeout_AndNacksSender()
    {
        // The prod shape: the post is issued and its Task simply never completes.
        var never = new TaskCompletionSource();
        var nacks = new List<(string Message, ErrorType Type)>();
        var scheduler = new TestScheduler();
        var terminated = false;

        RoutingGrain.PostToStream(
                post: () => never.Task,
                addressPath: "portal/user-1",
                deliveryId: "d1",
                sender: Sender,
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: NullLogger.Instance,
                timeout: Timeout,
                scheduler: scheduler)
            .Subscribe(_ => { }, () => terminated = true);

        // Before the bound elapses: still in flight, nobody has been told anything. Correct —
        // a slow post is not a failed post.
        scheduler.AdvanceBy(Timeout.Ticks - 1);
        nacks.Should().BeEmpty("a post that has not yet exceeded its bound is not a failure");
        terminated.Should().BeFalse();

        scheduler.AdvanceBy(2);

        // 🚨 The two things that must ALWAYS happen, and did not before #1028:
        terminated.Should().BeTrue(
            "the leg must complete so the routing pool slot and the in-flight route count are "
            + "released — a never-terminating leg is how a bounded pool silently fills up");
        var nack = nacks.Should().ContainSingle(
            "a delivery that neither lands nor reports is exactly the silent hang #1028 was made of "
            + "— the sender must get a DeliveryFailure so its Observe fires OnError").Subject;
        nack.Type.Should().Be(ErrorType.Failed);
        nack.Message.Should().Contain("portal/user-1");
    }

    [Fact]
    public async Task PostThatFaults_NacksSender_AndStillCompletes()
    {
        var nacks = new List<(string Message, ErrorType Type)>();

        await RoutingGrain.PostToStream(
                post: () => Task.FromException(new InvalidOperationException("stream provider down")),
                addressPath: "portal/user-2",
                deliveryId: "d2",
                sender: Sender,
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: NullLogger.Instance,
                timeout: Timeout)
            .ToTask();

        var nack = nacks.Should().ContainSingle().Subject;
        nack.Type.Should().Be(ErrorType.Failed);
        nack.Message.Should().Contain("stream provider down");
    }

    [Fact]
    public async Task PostThatSucceeds_CompletesWithoutNack()
    {
        var nacks = new List<(string Message, ErrorType Type)>();

        var result = await RoutingGrain.PostToStream(
                post: () => Task.CompletedTask,
                addressPath: "portal/user-3",
                deliveryId: "d3",
                sender: Sender,
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: NullLogger.Instance,
                timeout: Timeout)
            .ToTask();

        result.Should().Be(Unit.Default);
        nacks.Should().BeEmpty("a delivered post must never NACK the sender");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the structural half of the fix. This is the SHAPE the routing grain's
    /// turn used to have — <c>return s.OnNextAsync(delivery).ContinueWith(...)</c> — and it shows
    /// why nothing bounded it: the turn's completion IS the post's completion, so a post that never
    /// completes is a turn that never returns. <c>RoutingGrain</c> is <c>[StatelessWorker(1)]</c>
    /// and non-reentrant, so that one turn is the silo's ONLY routing turn; Orleans' request
    /// timeout applies to messages WAITING on a grain, never to the turn itself. Prod:
    /// <c>06:00:22</c> executing, <c>NonReentrancyQueueSize=541</c>.
    ///
    /// <para>The cure is that <c>RouteMessage</c> no longer returns anything derived from the
    /// delivery work — see <see cref="RoutingGrainTurnIsolationTest"/>, which proves it against a
    /// real cluster.</para>
    /// </summary>
    [Fact]
    public async Task NegativeControl_AwaitingThePostInsideTheTurn_NeverReturns()
    {
        var never = new TaskCompletionSource();

        // The pre-#1028 turn body, verbatim in shape.
        Task<IMessageDelivery> LegacyTurn() =>
            never.Task.ContinueWith(_ => (IMessageDelivery)new MessageDelivery<string>(), TaskScheduler.Default);

        var turn = LegacyTurn();

        // A "wait to confirm nothing happened" negative test — there is no positive signal to
        // filter for, because the whole point is that the turn produces none.
        var settled = await Task.WhenAny(turn, Task.Delay(TimeSpan.FromMilliseconds(250)));

        settled.Should().NotBe(turn,
            "this is the defect: the turn's completion is the post's completion, so a post that "
            + "never completes holds the silo's single non-reentrant routing turn forever, with no "
            + "timeout anywhere on that path (issue #1028)");
    }
}
