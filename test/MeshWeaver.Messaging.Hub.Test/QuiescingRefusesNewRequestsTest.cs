using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 A hub that has entered <see cref="MessageHubRunLevel.Quiescing"/> cannot SERVE a new request,
/// so it must not ACCEPT one — it answers, transiently, at the door.
///
/// <para><b>What this closes.</b> Quiescing exists so the replies a hub is still OWED can come in,
/// and <c>ScheduleNotify</c>'s intake gate only refused from <c>DisposeHostedHubs</c> on. Between
/// the two, a hub whose quiesce had already finished still admitted brand-new work — and built
/// hosted hubs to serve it. Measured on #3261's branch (<c>DanglingNodeTypeUpdateTest</c>,
/// 2026-09-04): a node hub logged <c>[QUIESCE-OK] … drained 0 callback(s)</c> and then took three
/// more <c>SubscribeRequest</c>s and created fresh <c>sync/*</c> children for them. Exactly one was
/// answered; the others died with the hub, and each requester held a pending callback until ITS own
/// quiesce budget expired — the 2 s × 72 disposals #3261 measured, entirely silently.</para>
///
/// <para><b>Why a NACK and not a drop.</b> A drop is the same silence with a different name. The
/// verdict is <see cref="ErrorType.ShuttingDown"/>, i.e. TRANSIENT: the address may reactivate, so
/// the requester is told to ask again rather than being handed a terminal answer.</para>
///
/// <para><b>Both tests hold the victim IN Quiescing on purpose</b>, by making it owe a reply a sink
/// deliberately withholds. Without that the phase is over in a millisecond and the late request
/// gets a routing <c>NotFound</c> for a hub that is already gone — a green that measured nothing.
/// </para>
/// </summary>
public class QuiescingRefusesNewRequestsTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record LateRequest : IRequest<LateResponse>;

    private record LateResponse;

    private record ParkedRequest : IRequest<ParkedResponse>;

    private record ParkedResponse;

    private static readonly Address VictimAddress = new("quiescing-victim", "1");

    private static readonly Address SinkAddress = new("quiescing-sink", "1");

    /// <summary>
    /// A request that arrives after Quiescing began is refused at the door. The victim genuinely
    /// owes a reply, so its Quiescing phase is really waiting; and the handler for the late request
    /// WOULD answer, so a pass can only come from the refusal — never from the request quietly
    /// being served after all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARequestArrivingAfterQuiescingBegan_IsRefusedAtTheDoor_NotAdmittedAndAbandoned()
    {
        var host = GetHost();
        var victim = await BuildVictimOwingAReply(host);

        victim.Hub.Dispose();
        await victim.ReachedQuiescing;

        var refusal = await Assert.ThrowsAsync<DeliveryFailureException>(
            () => host.Observe<LateResponse>(new LateRequest(), o => o.WithTarget(VictimAddress))
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(TestContext.Current.CancellationToken));

        Output.WriteLine(refusal.Message);
        refusal.Message.Should().Contain("shutting down",
            "the verdict must be TRANSIENT — the address may reactivate, so the requester is told "
            + "to ask again, never handed a terminal answer it would cache");
        refusal.Message.Should().NotContain("No route found",
            "a routing miss would mean the hub was already GONE when we posted — the window this "
            + "test is about would not have been exercised at all");

        victim.AnswerNow();
    }

    /// <summary>
    /// The anti-vacuity anchor. Quiescing exists to let the replies this hub is OWED come in; a
    /// refusal that also turned those away would deadlock every teardown while looking like a fix.
    /// The sink answers only once the victim is quiescing, so the reply crosses the very gate the
    /// test above proves is closed to new requests.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReplyToThisHubsOwnRequest_IsStillAdmitted_WhileItQuiesces()
    {
        var host = GetHost();
        var victim = await BuildVictimOwingAReply(host);

        victim.Hub.Dispose();
        await victim.ReachedQuiescing;

        victim.AnswerNow();

        (await victim.Owed).Should().NotBeNull(
            "a reply the quiescing hub is OWED must still be admitted — that is what the phase is "
            + "for, and refusing it would strand every teardown that is waiting on one");
    }

    private sealed record Victim(
        IMessageHub Hub,
        Task ReachedQuiescing,
        Task<IMessageDelivery<ParkedResponse>> Owed,
        Action AnswerNow);

    /// <summary>
    /// A victim that OWES a reply — so its Quiescing phase genuinely waits — plus a handler that
    /// WOULD answer a late request, and a sink that answers the owed one only on demand.
    /// </summary>
    private async Task<Victim> BuildVictimOwingAReply(IMessageHub host)
    {
        var sinkReceived = new AsyncSubject<Unit>();
        IMessageDelivery<ParkedRequest>? parked = null;

        var sink = host.GetHostedHub(SinkAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(ParkedRequest), typeof(ParkedResponse))
            .WithHandler<ParkedRequest>((_, d) =>
            {
                parked = d;
                sinkReceived.OnNext(Unit.Default);
                sinkReceived.OnCompleted();
                return d.Processed();
            }));
        sink.Should().NotBeNull();

        var hub = host.GetHostedHub(VictimAddress, c => c
            // Both hubs post as System: PostPipeline fails an application post CLOSED when no
            // AccessContext is set, and the victim's own request to the sink is what holds it in
            // Quiescing — without an identity it never leaves, and the phase has nothing to wait on.
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(LateRequest), typeof(LateResponse),
                typeof(ParkedRequest), typeof(ParkedResponse))
            .WithHandler<LateRequest>((h, d) =>
            {
                h.Post(new LateResponse(), o => o.ResponseFor(d));
                return d.Processed();
            }));
        hub.Should().NotBeNull();

        // Armed BEFORE Dispose, so the transition cannot be missed between the two calls.
        var reachedQuiescing = hub!.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Quiescing)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        var owed = hub.Observe<ParkedResponse>(new ParkedRequest(), o => o.WithTarget(SinkAddress))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        // The callback must be REGISTERED before we dispose, or Quiescing has nothing to wait for
        // and the hub is gone before the late request can reach its door.
        await sinkReceived.Should().Within(TestTimeouts.Convergence).Emit(
            "the sink must be holding the victim's request, so the victim owes a reply");

        return new Victim(hub, reachedQuiescing, owed, () =>
        {
            var delivery = parked;
            delivery.Should().NotBeNull("the sink must have received the parked request");
            sink!.Post(new ParkedResponse(), o => o.ResponseFor(delivery!));
        });
    }
}
