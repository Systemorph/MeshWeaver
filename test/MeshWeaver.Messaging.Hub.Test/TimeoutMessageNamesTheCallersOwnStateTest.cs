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
            .WithHandler<NeverAnswered>((_, delivery) => delivery.Processed());

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
}
