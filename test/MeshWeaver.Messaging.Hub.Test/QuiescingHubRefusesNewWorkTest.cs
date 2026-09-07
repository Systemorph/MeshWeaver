using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The intake gate used to open ONE PHASE TOO LATE (issue #3506).
///
/// <para><b>The defect.</b> <c>MessageService.ScheduleNotify</c>'s shutdown gate refused new
/// deliveries only from <c>MessageHubRunLevel.DisposeHostedHubs</c> onward, so the whole
/// <c>Quiescing</c> phase was wide open. A hub that had entered <c>[QUIESCE-START]</c> — and even
/// one that had already logged <c>[QUIESCE-OK]</c>, having drained everything it owed — kept
/// accepting inbound requests, kept handling them, and kept registering response callbacks for the
/// sub-requests it posted while handling them. Work taken on after the drain has, by construction,
/// no drain left to finish it: the quiesce budget is already spent, the next phase cancels it, and
/// the requester gets a <c>HubDisposedBeforeResponseException</c> after waiting out its bound.</para>
///
/// <para><b>Field measurement</b> (three independent bake runs, 2026-09-06, counted off the
/// <c>RECEIVED runLevel=…</c> stage in the request-fate trails <c>[QUIESCE-TIMEOUT]</c> prints):
/// SIX of NINE pending callbacks at the timeout had been taken on by a hub that was already
/// <c>Quiescing</c>. Each cost the full quiesce budget and ended in <c>forcibly cancelling</c> —
/// which is the tell of an unfixed root, and this is the root.</para>
///
/// <para><b>🚨 Not the fix, and not what this pins.</b> Widening <c>QuiesceTimeout</c>: #3261
/// settled that a bigger budget converts a leaked callback into a slower leaked callback. Nor is it
/// "quiesce later". Teardown must let ACCEPTED work FINISH (Doc/Architecture/TeardownLayers) — the
/// second test here is the half that says so, and it is not optional: a fix that refused the reply
/// the drain is waiting for would reintroduce the very <c>[QUIESCE-TIMEOUT]</c> it removes.</para>
///
/// <para><b>Why the victim is HELD in <c>Quiescing</c> rather than caught there.</b> The window is
/// a live race in production and would be a flake here, so the fixture creates it deterministically
/// instead: the victim posts a request to a hub that parks it, so it enters the drain owing one
/// callback and CANNOT advance to <c>DisposeHostedHubs</c> until that callback settles. The
/// generous <c>WithQuiesceTimeout</c> is not a widened bound — nothing waits for it, and the second
/// test asserts disposal completes far INSIDE it, which is the positive signal that the hub left
/// the phase by DRAINING (<c>[QUIESCE-OK]</c>) rather than by timing out.</para>
/// </summary>
public class QuiescingHubRefusesNewWorkTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A request the sink hub parks: received, and not answered until the test releases it.</summary>
    private record ParkedRequest : IRequest<ParkedResponse>;

    private record ParkedResponse;

    /// <summary>
    /// New work arriving at the victim. Its handler ANSWERS, so a pass can only come from the gate
    /// — never from the request quietly being served after all.
    /// </summary>
    private record NewWorkRequest : IRequest<NewWorkResponse>;

    private record NewWorkResponse;

    private static readonly Address VictimAddress = new("quiescing-victim", "1");

    private static readonly Address SinkAddress = new("quiescing-sink", "1");

    /// <summary>
    /// Long enough that the victim stays in <c>Quiescing</c> for the whole test — the phase under
    /// test is the fixture, not a deadline anything waits for. Every assertion below completes in
    /// well under <see cref="TestTimeouts.Convergence"/>, and the disposal assertion proves the hub
    /// left the phase by draining rather than by this elapsing.
    /// </summary>
    private static readonly TimeSpan HeldQuiesceBudget = TimeSpan.FromMinutes(2);

    /// <summary>Whether the victim's <see cref="NewWorkRequest"/> handler ran. It must not.</summary>
    private sealed class HandlerRan
    {
        private int ran;

        public void Record() => Interlocked.Exchange(ref ran, 1);

        public bool Did => Volatile.Read(ref ran) == 1;
    }

    /// <summary>
    /// The pin. A hub inside the quiesce drain must refuse a NEW correlated request with the
    /// transient, owner-minted <c>ShuttingDown</c> NACK — never accept work whose answer the next
    /// phase will cancel, and never drop it silently.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task NewCorrelatedRequest_IsRefused_WhileTheHubIsDrainingItsQuiesce()
    {
        var fixture = await ArrangeQuiescingVictim();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(
            () => RequestNewWork(fixture.Host));

        failure.Failure.Should().NotBeNull();
        Output.WriteLine(
            $"NACK: errorType={failure.Failure!.ErrorType} message={failure.Failure.Message}");

        // TRANSIENT: the address may reactivate (recycle / restart), so a terminal classification
        // would kill every consumer's rehydrate path — SynchronizationStream's resubscribe latch,
        // MeshNodeStreamCache's shutdown-drop handling and PackageInstaller's retry all key off
        // exactly this ErrorType and treat anything else as gone-for-good.
        failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "a quiescing hub is not a deleted address — the refusal must read 'ask again', not 'gone'");

        // 🚨 #3017 — and recognisable as THIS OWNER's answer, not merely as some transient failure.
        // A caller discriminating "the owner refused me" from "the routing layer could not reach
        // it" reads ShutdownNack.IsAnsweredByOwner, so the banner is asserted against the REAL
        // message rather than against a copy of its wording.
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, VictimAddress).Should().BeTrue(
            $"the owner at {VictimAddress} refused this delivery at the door, so its own banner "
            + "must be in the message");

        // The phase is IN the NACK, and that is what makes this a pin on #3506 rather than on the
        // pre-existing DisposeHostedHubs tier: before the fix this request was ACCEPTED at
        // Quiescing, ran its handler and came back a NewWorkResponse.
        failure.Failure.Message.Should().Contain($"RunLevel={MessageHubRunLevel.Quiescing}",
            "the gate that refused this is the QUIESCING tier — the DisposeHostedHubs tier was "
            + "always closed and would name itself instead");

        fixture.NewWork.Did.Should().BeFalse(
            "the gate refuses at INTAKE, so the handler must never have run — a hub that took the "
            + "work on and only then NACKed would have done the very thing #3506 is about");

        await fixture.ReleaseAndDispose();
    }

    /// <summary>
    /// The other half, and the reason the gate's <c>Quiescing</c> tier is narrower than its
    /// <c>DisposeHostedHubs</c> tier: a reply that SETTLES work accepted before the drain is exactly
    /// what the drain is waiting for. Refusing it would trade one silence for another and make
    /// every quiesce end in <c>[QUIESCE-TIMEOUT] … forcibly cancelling</c> — the symptom #3506
    /// exists to remove, reintroduced by its own fix.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ReplySettlingWorkAcceptedBeforeQuiesce_StillLands_AndTheDrainCompletes()
    {
        var fixture = await ArrangeQuiescingVictim();

        // The victim registered this callback while it was Started; the reply arrives while it is
        // Quiescing and must be let in.
        fixture.ReleaseParkedRequest();

        var settled = await fixture.PendingReply;
        settled.Message.Should().BeOfType<ParkedResponse>(
            "the reply correlates to a callback the victim registered BEFORE it began disposing — "
            + "the drain is waiting for precisely this, so the intake gate must not refuse it");

        // 🚨 The positive, specific signal that the hub left Quiescing by DRAINING: disposal
        // completes inside Convergence, orders of magnitude short of the held quiesce budget. A
        // refused reply would instead park until HeldQuiesceBudget and end in [QUIESCE-TIMEOUT].
        await fixture.Victim.DisposalCompleted.FirstOrDefaultAsync().Await()
            .WaitAsync(TestTimeouts.Convergence);
        fixture.Victim.RunLevel.Should().Be(MessageHubRunLevel.Dead);
    }

    private sealed record QuiescingVictim(
        IMessageHub Host,
        IMessageHub Victim,
        HandlerRan NewWork,
        Task<IMessageDelivery<ParkedResponse>> PendingReply,
        Action ReleaseParkedRequest)
    {
        /// <summary>Lets the held victim finish its drain so the fixture tears down cleanly.</summary>
        public async Task ReleaseAndDispose()
        {
            ReleaseParkedRequest();
            await PendingReply;
            await Victim.DisposalCompleted.FirstOrDefaultAsync().Await()
                .WaitAsync(TestTimeouts.Convergence);
        }
    }

    /// <summary>
    /// Builds a victim that is deterministically HELD inside the quiesce drain: it owes exactly one
    /// response callback, parked at a sink hub that will not answer until the test says so.
    /// </summary>
    private async Task<QuiescingVictim> ArrangeQuiescingVictim()
    {
        // 🚨 No hand-woven gate. The sink→test signal is an AsyncSubject the sink's handler
        // completes, awaited through the assertion helper; the release is a plain Post from the
        // test thread, so nothing here parks a worker and nothing needs a flag.
        var parkedArrived = new AsyncSubject<IMessageDelivery>();
        IMessageDelivery? parked = null;
        var newWork = new HandlerRan();

        var host = GetHost();

        var sink = host.GetHostedHub(SinkAddress, c => c
            // Plumbing-only fixture with no signed-in user, exactly like the hubs HubTestBase
            // configures for itself: posts carry the System identity rather than none. Without it
            // the never-null AccessContext invariant fails every post these two hubs make.
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(ParkedRequest), typeof(ParkedResponse))
            // Receives and HOLDS. Processed() without a reply leaves the victim's callback pending,
            // which is the whole fixture: a hub with something left to drain cannot leave Quiescing.
            .WithHandler<ParkedRequest>((_, d) =>
            {
                parked = d;
                parkedArrived.OnNext(d);
                parkedArrived.OnCompleted();
                return d.Processed();
            }));
        sink.Should().NotBeNull();

        var victim = host.GetHostedHub(VictimAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithTypes(typeof(ParkedRequest), typeof(ParkedResponse),
                typeof(NewWorkRequest), typeof(NewWorkResponse))
            .WithQuiesceTimeout(HeldQuiesceBudget)
            // WOULD answer — so a refusal can never be confused with "nobody handled it".
            .WithHandler<NewWorkRequest>((h, d) =>
            {
                newWork.Record();
                h.Post(new NewWorkResponse(), o => o.ResponseFor(d));
                return d.Processed();
            }));
        victim.Should().NotBeNull();

        // The victim issues a request it will still be awaiting when disposal starts. Observe
        // registers the response subject BEFORE posting, so the callback exists from here on.
        var pendingReply = victim!
            .Observe(new ParkedRequest(), o => o.WithTarget(SinkAddress))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        await parkedArrived.Should().Within(TestTimeouts.Convergence).Emit(
            "the sink must be holding the victim's request, so the victim owes exactly one callback "
            + "and cannot drain past Quiescing on its own");

        // Dispose is a POST, not a blocking teardown: it returns and the phases advance on the
        // victim's own turn loop.
        victim.Dispose();

        // Wait on the CONDITION — the phase itself — never on a clock. RunLevelChanged replays the
        // current level, so this is correct whether or not the transition has already happened.
        await victim.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Quiescing)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        victim.RunLevel.Should().Be(MessageHubRunLevel.Quiescing,
            "the victim owes a callback nobody has answered, so it is INSIDE the drain — had it "
            + "advanced, this test would be exercising the pre-existing DisposeHostedHubs gate "
            + "and would prove nothing about #3506");

        return new QuiescingVictim(
            host,
            victim,
            newWork,
            pendingReply,
            () => sink!.Post(new ParkedResponse(), o => o.ResponseFor(parked!)));
    }

    /// <summary>
    /// Posts a NEW request from the host to the quiescing victim and awaits its outcome — a
    /// response if the gate let it in (the pre-#3506 behaviour), a DeliveryFailure if it refused.
    /// </summary>
    private static Task<IMessageDelivery<NewWorkResponse>> RequestNewWork(IMessageHub host) =>
        host.Observe(new NewWorkRequest(), o => o.WithTarget(VictimAddress))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
}
