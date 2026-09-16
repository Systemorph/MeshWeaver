using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A request timeout must describe the hub that gave up, not guess about the one that did
/// not answer.</b>
///
/// <para>The message used to end <i>"The request may have been undeliverable or the target hub was
/// not found"</i> — two buckets, asserted as though they were exhaustive. They are not. The third
/// possibility is the one a reader most needs ruled out first: <b>this hub never PROCESSED a
/// response that did arrive</b>, because its own action block was busy, gated, or backed up. A
/// single-threaded actor that is wedged looks, from inside, exactly like a peer that never
/// replied.</para>
///
/// <para><b>Measured on production, 2026-09-02.</b> Opening a document failed with that message
/// naming <c>cache/…</c> as the waiting hub and a node path as the target. <i>Both</i> buckets it
/// offered were wrong: the node existed (version 53, edited the previous evening) and its hub
/// resolved fine — one per-node hub was wedged, and a recycle cleared it with no data loss. The
/// message sent the reader to "does this node exist?", the one question that was never in doubt,
/// and that deployment ships no logs to Log Analytics, so there was nothing else to read
/// (MeshWeaver#2896, open for weeks as "write verdict unconfirmed" for exactly this reason).</para>
/// </summary>
public class TimeoutMessageNamesTheCallersOwnStateTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record NeverAnswered : IRequest<NeverAnswer>;
    private record NeverAnswer;

    /// <summary>
    /// A handler that receives the request and deliberately answers nothing, so the requester can
    /// only end by the HUB's own <c>RequestTimeout</c> — which is the message under test. Answering
    /// nothing is the point: an Rx-level <c>.Timeout(...)</c> would raise Rx's own exception and
    /// never reach <c>BuildTimeoutMessage</c> at all.
    ///
    /// <para>The bound is <c>TestTimeouts.Quick</c>, not a literal: it scales with the host under
    /// the same factor as every other wait here, and a hand-written one would spend budget from
    /// <c>TestTimeoutLiteralRatchetGuard</c>, whose inventory may only shrink. These tests also
    /// carry no <c>[Fact(Timeout = …)]</c> — the hub's own <c>RequestTimeout</c> IS the thing under
    /// test and already bounds them, and xunit's <c>methodTimeout</c> is the outer net. A second,
    /// hand-written bound would be a guess about machine speed with nothing left to catch.</para>
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRequestTimeout(TestTimeouts.Quick)
            .WithHandler<NeverAnswered>((_, delivery) => delivery.Processed())
            // Handled and answered nothing — its only job is to make this hub take turns while the
            // request above is outstanding, so the interval count has something to count.
            .WithHandler<Noise>((_, delivery) => delivery.Processed());

    [Fact]
    public async Task TheTimeoutMessage_NamesThisHubsRunLevelAndQueue_AndDoesNotClaimTwoCausesAreExhaustive()
    {
        var host = GetHost();

        var act = async () => await host
            .Observe<NeverAnswer>(new NeverAnswered(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        var message = (await act.Should().ThrowAsync<TimeoutException>()).Which.Message;

        // The request is still named — the property the previous form already had, and which a
        // rewrite must not lose.
        message.Should().Contain("NeverAnswered", "the timed-out request type must still be named");

        // 🚨 The addition: this hub's OWN state, so a reader can tell "I heard nothing" from
        // "I never got round to processing what I heard".
        message.Should().Contain("This hub:", "the message must describe the hub that gave up");
        message.Should().Contain("RunLevel=", "the caller's run level is half the discriminator");
        message.Should().Contain("Queue(buffer=", "the caller's queue depth is the other half");

        // 🚨 The removal: the old sentence presented two causes as the whole set. Whatever the
        // message says now, it must not resurrect that claim.
        message.Should().NotContain("may have been undeliverable",
            "asserting two buckets as exhaustive is what sent the production reader to the wrong "
            + "question; the message must state its uncertainty instead of guessing");
    }

    /// <summary>
    /// 🚨 <b>The silent regression this rewrite could have caused.</b>
    ///
    /// <para><c>MeshNodeStreamCache.IsTransientOwnerFailure</c> and
    /// <c>AreaErrorClassifier.IsTransientHubFailure</c> both decide RETRYABILITY by substring, and
    /// the old message matched three of their markers: <c>"No response received in hub"</c>,
    /// <c>"target hub was not found"</c> and <c>"undeliverable"</c>. The rewrite deliberately drops
    /// the last two. That is safe only because the first survives — and nothing would have said so
    /// if it had not: <c>MessageService</c> notes in its own comment that a violation of this
    /// coupling is <i>silent</i> — no compiler error, no exception, just a read that stops being
    /// retried and waits out its budget instead.</para>
    ///
    /// <para>So this asserts the marker directly. It is a string assertion because the coupling
    /// IS a string coupling; making it look like anything else would misrepresent how fragile it
    /// is.</para>
    /// </summary>
    [Fact]
    public async Task TheTimeoutMessage_KeepsTheMarkerThatClassifiesItAsTransient()
    {
        var host = GetHost();

        var act = async () => await host
            .Observe<NeverAnswer>(new NeverAnswered(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        var message = (await act.Should().ThrowAsync<TimeoutException>()).Which.Message;

        message.Should().Contain("No response received in hub",
            "both IsTransientOwnerFailure and IsTransientHubFailure classify a hub timeout as "
            + "RETRYABLE on this substring. The rewrite dropped the other two markers it used to "
            + "carry ('target hub was not found', 'undeliverable'), so this one is now the only "
            + "thing keeping a timed-out read retryable. Losing it would make every such read "
            + "terminal — silently, with no compiler error and no exception.");
    }

    /// <summary>
    /// 🚨 <b>The idleness claim was an INSTANTANEOUS SAMPLE asserted over an INTERVAL
    /// (MeshWeaver#1174).</b>
    ///
    /// <para>The queue snapshot is read at the moment the wait gives up, and the sentence
    /// generalised it across the whole <c>RequestTimeout</c>: a hub saturated for the first
    /// 59 seconds and drained in the 60th printed <i>"This hub was idle while waiting, so it
    /// processed everything delivered to it"</i> — a claim the sample cannot support. On #1174
    /// that sentence, read literally, retired the only live hypothesis twice.</para>
    ///
    /// <para><c>Version</c> is incremented once per message this hub handles, so the difference
    /// against the value captured when the wait began is the interval fact the sample is not.
    /// This test makes the hub handle messages DURING the wait and asserts the count is reported
    /// and non-zero — with the queue empty at the end, which is exactly the shape production
    /// reported.</para>
    /// </summary>
    [Fact]
    public async Task TheTimeoutMessage_CountsWhatThisHubHandledWhileWaiting_NotJustItsQueueAtTheEnd()
    {
        var host = GetHost();

        var pending = host
            .Observe<NeverAnswer>(new NeverAnswered(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync();

        // Work this hub provably handles WHILE the request above is outstanding, and which is all
        // drained long before the bound elapses — so the queue is empty at the give-up instant and
        // the snapshot alone would report "idle".
        for (var i = 0; i < 25; i++)
            host.Post(new Noise(i), o => o.WithTarget(CreateHostAddress()));

        var act = async () => await pending.Await(TestContext.Current.CancellationToken);
        var message = (await act.Should().ThrowAsync<TimeoutException>()).Which.Message;

        message.Should().Contain("handledWhileWaiting=",
            "the interval — how many messages this hub handled while the request was outstanding — "
            + "is what tells a genuinely idle hub from one that was busy and happened to be empty "
            + "at the instant it gave up; the snapshot alone cannot.");
        message.Should().NotContain("handledWhileWaiting=0",
            "this hub demonstrably handled the Noise messages during the wait, so a zero here would "
            + "mean the counter is not measuring the interval at all");
        message.Should().NotContain("was idle while waiting, so it processed everything delivered to it",
            "that sentence asserts an interval property from a single end-of-wait sample. It is the "
            + "exact wording that closed MeshWeaver#1174 on a wrong verdict.");
    }

    /// <summary>
    /// 🚨 <b>On a SELF-ADDRESSED request every candidate the message offered is inapplicable, and
    /// so is the discriminator it sent the reader to (MeshWeaver#1174).</b>
    ///
    /// <para>The mesh's node CRUD runs on <c>portal/nodeops-{meshId}</c> and <c>MeshService</c>
    /// ISSUES those requests on that same hub, so sender and target are ONE hub: there is no
    /// routing leg to lose the request and no reply leg to lose the answer, and <i>"the target's
    /// own RunLevel and queue"</i> are the numbers already printed in the same sentence.
    /// Production read the old text literally and concluded the request "never reached the
    /// queue" — which an empty queue at the give-up instant does not imply, because the canonical
    /// mesh handlers return <c>Processed()</c> at once and owe their reply from a DETACHED
    /// observable.</para>
    ///
    /// <para>This test IS that production shape: the handler provably RECEIVES the request (it is
    /// registered and returns <c>Processed()</c>) and answers nothing. Any message that still
    /// offers "the target never received the request" as a live candidate here is offering a
    /// candidate this very test falsifies.</para>
    /// </summary>
    [Fact]
    public async Task TheTimeoutMessage_OnASelfAddressedRequest_DoesNotOfferRoutingOrALostReply()
    {
        var host = GetHost();

        var act = async () => await host
            .Observe<NeverAnswer>(new NeverAnswered(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        var message = (await act.Should().ThrowAsync<TimeoutException>()).Which.Message;

        message.Should().Contain("THIS HUB IS ALSO THE TARGET",
            "sender == target is the single most common node-CRUD shape in the mesh and it changes "
            + "which causes are even possible; the message must say so rather than print a generic "
            + "three-way unknown");
        message.Should().NotContain("the target never received the request (routing)",
            "there is no routing leg on a self-addressed delivery — this test's handler provably "
            + "RECEIVED the request, so offering that candidate is offering a falsified one");
        message.Should().NotContain("the target's own RunLevel and queue can",
            "for a self-addressed request those are the numbers already printed above, so sending "
            + "the reader to measure them is an instruction that cannot be followed");
    }

    /// <summary>
    /// 🚨 <b>"This message cannot distinguish them" was true only because the message did not
    /// look (MeshWeaver#1174).</b>
    ///
    /// <para><c>RequestFateLedger</c> is per hub TREE and records every stage this delivery passed
    /// through — intake, gate, routing, handler entry, the handler's own detached stages, the
    /// reply's journey — ending in a one-sentence verdict naming which shape it is. The hub that
    /// builds this message OWNS that ledger. Printing it is one lookup.</para>
    /// </summary>
    [Fact]
    public async Task TheTimeoutMessage_CarriesTheRequestsOwnStageTrail()
    {
        var host = GetHost();

        var act = async () => await host
            .Observe<NeverAnswer>(new NeverAnswered(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        var message = (await act.Should().ThrowAsync<TimeoutException>()).Which.Message;

        message.Should().Contain("Trail:",
            "the stage trail is what turns this from a report of silence into a report of WHERE the "
            + "silence began");
        message.Should().Contain("HANDLER_ENTER",
            "the handler in this fixture demonstrably ran, and the trail is the only part of the "
            + "message that can say so — the queue snapshot cannot");
        message.Should().Contain("⇒",
            "the ledger's own verdict sentence follows the stages; without it a reader gets the "
            + "evidence and not the conclusion");
    }

    /// <summary>Fire-and-forget noise the host handles while a request is outstanding.</summary>
    private record Noise(int N);
}
