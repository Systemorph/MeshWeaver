// <meshweaver>
// Id: Testing/MessagingHub/ARefusedReplyReachesItsRequesterTest
// DisplayName: Testing/MessagingHub/ARefusedReplyReachesItsRequesterTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Systemorph/MeshWeaver#4170, the INTAKE-GATE leg — a reply is an ANSWER at every tier.
///
/// <para><b>What was measured.</b> A <c>PatchDataResponse</c> the owner had already minted
/// (<c>PATCH_MERGE_STAMPED v=3 … PATCH_ACK ok</c>) entered that owner's OWN intake at
/// <c>RunLevel=Quiescing</c> — where tier 1 exempts a correlated delivery as *"an ANSWER, never new
/// work"* — and was refused 0 ms later by tier 2, the run level having advanced to
/// <c>DisposeHostedHubs</c> between the stamp and the check. The abandonment path then answered
/// <c>delivery.Sender</c>, which for a reply is the RESPONDER: a <c>DeliveryFailure</c> the hub
/// posted to itself, forwarded through its parent, delivered back to nobody
/// (<c>RESPONSE_ARRIVED_NO_SUBJECT</c>), while the actual requester burned its whole 31 s bound
/// (<c>OwnerUnreachable</c>). Twice on core's merge queue in one morning.</para>
///
/// <para><b>The two halves pinned here.</b> (a) from <c>DisposeHostedHubs</c> until
/// <c>ShutDown</c> a correlated reply is ADMITTED and routed — it registers no callback, owes no
/// work, and is addressed elsewhere, so tier 2's "no new work" does not reach it; (b) past
/// <c>ShutDown</c>, where admitting it would leak it into drained queues, the refusal is reported
/// to the REQUESTER — the party the reply was FOR, which the delivery names — as the transient
/// <see cref="ErrorType.ShuttingDown"/>, never as <see cref="ErrorType.Failed"/>: the work the
/// lost reply reported may have committed, so the outcome is unconfirmed, not failed.</para>
/// </summary>
public class ARefusedReplyReachesItsRequesterTest(MeshTestContext context) : InMeshTestBase(context)
{
    private record SlowRequest : IRequest<SlowAck>;

    private record SlowAck(string Marker);

    private record ParkTurn;

    private static readonly Address ResponderAddress = new("refused-reply-responder", "1");
    private static readonly Address ChildAddress = new("refused-reply-child", "1");
    private static readonly Address RequesterAddress = new("refused-reply-requester", "1");

    private const string Marker = "the-answer-that-was-minted";

    /// <summary>The responder: takes the request, replies to NOBODY (the detached-reply shape —
    /// the real one mints its verdict on a later turn), so the requester's callback stays pending
    /// and this hub's own pump stays free to advance its phases.</summary>
    private static MessageHubConfiguration Responder(MessageHubConfiguration c)
        => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(SlowRequest), typeof(SlowAck), typeof(ParkTurn))
            .WithHandler<SlowRequest>((_, d) => d.Processed());

    /// <summary>A child whose parked turn holds its owner at <c>DisposeHostedHubs</c> — the owner
    /// cannot leave that phase until its hosted hubs report done, which is the state this test
    /// needs and the state the production trail was taken in.</summary>
    private static MessageHubConfiguration ParkedChild(
        MessageHubConfiguration c, AsyncSubject<Unit> parked, Func<bool> released)
        => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(ParkTurn))
            .WithHandler<ParkTurn>((_, d) =>
            {
                parked.OnNext(Unit.Default);
                parked.OnCompleted();
                SpinWait.SpinUntil(released, TimeSpan.FromSeconds(60));
                return d.Processed();
            });

    /// <summary>The reply as the responder's own <c>Post</c> builds it: typed, targeted at the
    /// requester, carrying the correlation. Handed to the responder's intake exactly as an
    /// outbound post is — <c>Post</c> enters this hub's own pipeline before anything routes it.</summary>
    private IMessageDelivery ReplyFor(IMessageHub responder, Address requester, string requestId)
        => new MessageDelivery<SlowAck>(
            new SlowAck(Marker),
            new PostOptions(responder.Address)
                .WithTarget(requester)
                .WithProperty(PostOptions.RequestId, requestId),
            responder.JsonSerializerOptions);

    private (IMessageHub Responder, IMessageHub Requester, string RequestId, Task<IMessageDelivery> Response,
        AsyncSubject<Unit> Parked, Func<bool> Released, Action Release) Arrange(CancellationToken ct)
    {
        var parked = new AsyncSubject<Unit>();
        var releaseChild = 0;

        var requester = Mesh.GetHostedHub(RequesterAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(SlowRequest), typeof(SlowAck)), HostedHubCreation.Always)!;
        var responder = Mesh.GetHostedHub(ResponderAddress, Responder, HostedHubCreation.Always)!;
        responder.GetHostedHub(ChildAddress,
                c => ParkedChild(c, parked, () => Volatile.Read(ref releaseChild) == 1), HostedHubCreation.Always)
            .Should().NotBeNull();

        var requestId = Guid.NewGuid().ToString("N");
        var response = requester
            .Observe((object)new SlowRequest(), o => o.WithTarget(ResponderAddress), requestId)!
            .FirstAsync()
            .Await(ct);

        return (responder, requester, requestId, response, parked,
            () => Volatile.Read(ref releaseChild) == 1, () => Volatile.Write(ref releaseChild, 1));
    }

    /// <summary>
    /// (a) — the loss itself. A correlated reply handed to a responder that is AT
    /// <c>DisposeHostedHubs</c> is routed to its requester instead of being dropped in the
    /// responder's own intake.
    /// </summary>
    [MeshFact(TimeoutSeconds = 1)]
    public async Task AtDisposeHostedHubs_ACorrelatedReply_IsAdmittedAndReachesItsRequester()
    {
        var ct = CancellationToken.None;
        var (responder, requester, requestId, response, parked, _, release) = Arrange(ct);
        try
        {
            responder.Post(new ParkTurn(), o => o.WithTarget(ChildAddress));
            await parked.Should().Within(TestTimeouts.Convergence).Emit("the child's block must be parked");

            responder.Dispose();
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Where(_ => responder.RunLevel == MessageHubRunLevel.DisposeHostedHubs)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            requester.RunLevel.Should().Be(MessageHubRunLevel.Started, "the requester is alive and waiting");
            Output.WriteLine($"[fence] responder={responder.RunLevel} requester={requester.RunLevel}");

            // THE ACT: the reply the responder minted, entering the responder's own intake.
            responder.DeliverMessage(ReplyFor(responder, requester.Address, requestId));

            var answer = await response.WaitAsync(TestTimeouts.Convergence, ct);
            answer.Message.Should().BeOfType<SlowAck>().Which.Marker.Should().Be(Marker,
                "a reply is an ANSWER at every tier — refusing it in the responder's own intake is "
                + "what left a committed write's caller waiting out its whole bound");
            Output.WriteLine($"trail: {Mesh.DescribeRequestFate(requestId)}");
        }
        finally
        {
            release();
        }
    }

    /// <summary>
    /// (b) — the residue. Past <c>ShutDown</c> the reply cannot be admitted (the queues are
    /// drained, so nothing would ever dequeue it), and the refusal is reported to the REQUESTER as
    /// transient — never to the responder itself, and never as a failure of the work.
    /// </summary>
    [MeshFact(TimeoutSeconds = 1)]
    public async Task PastShutDown_ARefusedReply_NacksTheRequesterTransiently_NotTheResponder()
    {
        var ct = CancellationToken.None;
        var (responder, requester, requestId, response, _, _, release) = Arrange(ct);
        try
        {
            responder.Dispose();
            await responder.DisposalCompleted.FirstOrDefaultAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            responder.RunLevel.Should().Be(MessageHubRunLevel.Dead, "past ShutDown, queues drained");

            responder.DeliverMessage(ReplyFor(responder, requester.Address, requestId));

            var failure = await Assert.ThrowsAsync<DeliveryFailureException>(
                () => response.WaitAsync(TestTimeouts.Convergence, ct));
            Output.WriteLine($"NACK: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");
            Output.WriteLine($"trail: {Mesh.DescribeRequestFate(requestId)}");

            failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
                "the work the lost reply reported may have COMMITTED, so the outcome is unconfirmed "
                + "and retryable — reporting Failed would make a caller re-apply a write that landed");
            ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, responder.Address).Should().BeTrue(
                "the requester must be able to tell this owner's refusal from a routing failure");
            Mesh.DescribeRequestFate(requestId).Should().Contain("REPLY_REFUSED_REQUESTER_NACKED",
                "the trail names the party that was told — the requester, never the responder");
        }
        finally
        {
            release();
        }
    }

    /// <summary>
    /// The answer-once control: a refused <see cref="DeliveryFailure"/> is NOT answered with
    /// another one.
    ///
    /// <para>🚨 A NACK carries <see cref="PostOptions.RequestId"/> exactly like any other reply, so
    /// the correlation test alone would mint a failure ABOUT a failure — and two concurrently
    /// disposing hubs doing that to each other is the ping-pong every guard on this path exists to
    /// prevent. <c>MayAnswer()</c> reads that contract off the envelope; without it this case
    /// resolves the requester's callback with a second NACK, which is how the guard is falsified.</para>
    /// </summary>
    [MeshFact(TimeoutSeconds = 1)]
    public async Task ARefusedDeliveryFailure_IsNotAnsweredWithAnotherOne()
    {
        var ct = CancellationToken.None;
        var (responder, requester, requestId, response, _, _, release) = Arrange(ct);
        try
        {
            responder.Dispose();
            await responder.DisposalCompleted.FirstOrDefaultAsync().Timeout(TestTimeouts.Convergence).Await(ct);

            var nack = new MessageDelivery<DeliveryFailure>(
                new DeliveryFailure(ReplyFor(responder, requester.Address, requestId))
                {
                    ErrorType = ErrorType.ShuttingDown,
                    Message = "an earlier refusal, correlated like any other reply"
                },
                new PostOptions(responder.Address)
                    .WithTarget(requester.Address)
                    .WithProperty(PostOptions.RequestId, requestId),
                responder.JsonSerializerOptions);
            responder.DeliverMessage(nack);

            await Observable.FromAsync(() => response).Should().NotEmit(TimeSpan.FromSeconds(3),
                "a refused NACK must not be answered with another NACK — the requester's callback "
                + "stays as it was, and the two hubs do not answer each other's refusals");
            Mesh.DescribeRequestFate(requestId).Should().NotContain("REPLY_REFUSED_REQUESTER_NACKED",
                "the reporting branch must not have run for a DeliveryFailure");
        }
        finally
        {
            release();
        }
    }
}
