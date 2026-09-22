using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro for the SILENT-ABANDONMENT hang: a hub that is disposed while a
/// request is still parked behind its initialization gates used to throw that request away
/// without telling anyone, so the sender's <c>hub.Observe(...)</c> had nothing to resolve on
/// and burned its entire request budget in total silence.
///
/// <para><b>Why this is a production bug, not a test artefact.</b> <see cref="DisposeRequest"/>
/// is deliberately exempt from the init gate (deferring it would break disposal) while an
/// ordinary request is NOT. So any recycle that lands during a hub's activation window jumps
/// the queue and annihilates the very request that triggered the activation — and every recycle
/// path is affected: <c>NodeTypeEnrichmentHelpers.WithOverlaySelfHeal</c>'s self-recycle,
/// <c>RecycleLayoutArea</c>, the MCP <c>recycle</c> tool, a node delete. The caller — a page
/// load, a <c>GetMeshNode</c> read — then spins for its whole budget with no error to show.</para>
///
/// <para><b>Field evidence.</b> <c>ThreadAgentIntegrationTest</c> on CI: the
/// <c>ACME/ProductLaunch</c> instance hub was created, handed the routed <c>GetDataRequest</c>,
/// and self-disposed 13 ms later via the overlay self-heal watcher. The reader sat idle for its
/// full 60 s (process memory flat throughout — nothing was running) and the timeout diagnostic
/// reported <c>Target: NO LOCAL HUB</c>.</para>
///
/// <para><b>The contract pinned here.</b> The abandoned delivery is NACKed through the PARENT
/// hub (our own Post would re-enter the disposing service's shutdown gate and be dropped) with
/// the TRANSIENT <see cref="ErrorType.ShuttingDown"/> — never NotFound, never a generic terminal
/// failure. A recycled address reactivates on the next access, so the sender must read this as
/// "ask again", not "gone"; consumers with their own recovery machinery (SynchronizationStream's
/// resubscribe latch) rely on exactly that classification.</para>
/// </summary>
public class DeferredDeliveryNackedOnDisposeTest : HubTestBase
{
    private readonly DeferredLog log = new();

    public DeferredDeliveryNackedOnDisposeTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(log);
            // Event 7301 is asserted at BOTH levels here, so the capture must be able to SEE a
            // Debug line — otherwise "the Error is gone" and "the line vanished entirely" read
            // identically and the self-addressed test would pass having checked nothing.
            l.AddFilter<DeferredLog>(null, LogLevel.Debug);
        });
    }

    private record GatedRequest : IRequest<GatedResponse>;

    private record GatedResponse;

    private static readonly Address GatedAddress = new("gated", "1");

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).WithTypes(typeof(GatedRequest), typeof(GatedResponse));

    [Fact(Timeout = 30_000)]
    public async Task DeferredRequest_IsNacked_WhenHubIsDisposedBeforeItsGateOpens()
    {
        var host = GetHost();

        // A hosted hub whose gate never opens — the activation window, held open forever so the
        // race is deterministic rather than millisecond-timed. It registers a handler that WOULD
        // answer, so a pass can only come from the NACK, never from the request being served.
        var gated = host.GetHostedHub(
            GatedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("test-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        // Pre-registers the callback, then posts. The request lands on the gated hub and defers.
        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(GatedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        // Wait until the request is DEMONSTRABLY parked in the deferred queue before recycling.
        // Without this the test could dispose before the delivery was ever accepted, which is the
        // already-handled intake-gate case (ScheduleNotify NACKs that one) — a different code path
        // that would pass even with the defect present.
        await WaitForDeferredBacklog(host);

        // The recycle. DisposeRequest bypasses the gate, so it overtakes the deferred request and
        // tears the hub down with the request still inside it.
        host.Post(new DisposeRequest(), o => o.WithTarget(GatedAddress));

        // WITHOUT the fix this task never completes and the [Fact] timeout fires with no
        // explanation — precisely the production symptom (a page that spins).
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        // TRANSIENT, so a recycled address reads as "retry", never as "gone".
        failure.Failure.Should().NotBeNull();
        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown);
        failure.Failure.Message.Should().Contain("test-never-opens",
            "the shutdown answer must name the gates recorded before teardown opened them");
        log.At(LogLevel.Error).Should().Contain(message => message.Contains("test-never-opens"),
            "event 7301 must preserve the gate that actually held the delivery (#3712), and a "
            + "delivery from ANOTHER hub strands a real waiter on a transient NACK — that stays "
            + "an Error (the control for the self-addressed case below, #4178)");

        failure.Failure.Message.Should().Contain("deferred",
            "the NACK must name WHY the message was abandoned — a bare failure sends the next "
            + "investigator hunting the wrong layer");
    }

    private static readonly Address SelfTalkerAddress = new("self-talker", "1");

    /// <summary>
    /// Systemorph/MeshWeaver#4178 — the <c>$model-probe</c> shape. A hub disposed with its OWN
    /// deferred request strands NOBODY, so event 7301 must report it at Debug, not Error.
    ///
    /// <para><b>Why it is not a silenced fault.</b> <c>NackThroughParent</c> declines this
    /// delivery one method down with <c>NACK_DECLINED reason=sender-is-self</c> — there is no
    /// external registry to post an answer to, because the pending-response registry that would
    /// resolve it is this hub's own and is cancelled in the same <c>Dispose</c>. So the Error's
    /// sentence "the sender is answered ShuttingDown" was false for this shape, and the defect it
    /// told the reader to hunt has no victim. Production measurement in #4178: 76 such lines per
    /// plugin-gate shard, every one a <c>$model-probe/{guid}</c> discarding its own
    /// <c>GetDataRequest</c> — a lifetime <c>TransientNodeProbe</c> declares by design.</para>
    ///
    /// <para><b>Negative control.</b> The Debug line is asserted POSITIVELY (it must exist, and
    /// name the gate), so "classified as teardown-normal" cannot be confused with "the site stopped
    /// reporting". The external-sender Error is pinned by the test above, on the same event id.</para>
    /// </summary>
    // No literal method timeout: every wait below carries TestTimeouts.Convergence, which is the
    // reasoned bound, and xunit.runner.json's methodTimeout bounds the body. A hand-written
    // `Timeout = 30_000` here would be a guessed wait on top of a measured one
    // (TestTimeoutLiteralRatchetGuard).
    [Fact]
    public async Task SelfAddressedDeferredRequest_IsTeardownNormal_NotAnError()
    {
        var host = GetHost();

        var selfTalker = host.GetHostedHub(
            SelfTalkerAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                // The hub posts on its own behalf below; PostPipeline fails closed with no
                // AccessContext, so the infrastructure identity is what a probe hub carries too.
                .WithPostingIdentity(PostingIdentity.System)
                .WithInitializationGate("self-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        selfTalker.Should().NotBeNull();

        // THE SHAPE: the hub posts to ITSELF, so Sender == Target == this hub's address —
        // byte-for-byte the production line's "Hub $model-probe/x … (from $model-probe/x)".
        selfTalker!.Post(new GatedRequest(), o => o.WithTarget(SelfTalkerAddress));

        await WaitForDeferredBacklog(host);

        host.Post(new DisposeRequest(), o => o.WithTarget(SelfTalkerAddress));
        await selfTalker.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        log.At(LogLevel.Debug).Should().Contain(m => m.Contains("self-gate-never-opens"),
            "the discard must STILL be reported and still name the gate that held it — a "
            + "reclassification that stops reporting would be a silenced fault, not a classification");
        log.At(LogLevel.Error).Should().NotContain(m => m.Contains("self-gate-never-opens"),
            "the sender IS this hub, so no answer is owed outside it and NackThroughParent declines "
            + "it as sender-is-self — an Error here alleges stranded work that has no waiter (#4178)");
    }

    private static readonly Address AttributedAddress = new("attributed", "1");

    private static readonly Address UnattributedAddress = new("unattributed", "1");

    /// <summary>The sentence an operator-style recycle writes onto its <see cref="DisposeRequest"/>.</summary>
    private const string TheStatedReason =
        "MeshOperations.Recycle: an operator asked for this hub to be recycled";

    /// <summary>
    /// Systemorph/MeshWeaver#3712 — the discard Error ends with an instruction to <i>"find why this
    /// hub disposed before its deferred work could run"</i>, and could not answer its own question.
    ///
    /// <para><b>The production shape.</b> <c>Admin/_LogIncident/d2249f800ffc2577</c>: 364
    /// occurrences between 2026-09-08 and 2026-09-14 across 13 pods. Every captured line names the
    /// message, its sender and the gates it sat behind — and not one says which teardown threw it
    /// away, so a reader given the incident cannot tell an operator recycle from a NodeType rebind
    /// from an owner's cascade. The hub HAS the answer: <c>disposeRequestedBy</c> /
    /// <c>disposeReason</c> are recorded one frame earlier so <c>[QUIESCE-START]</c> can print them
    /// (#3510's <i>"[QUIESCE-START] on a root should name who asked"</i>) — but that line is
    /// <c>Information</c> and the red-log pipeline files <c>Error</c>s, so the one line that becomes
    /// an ISSUE was the one line without the attribution.</para>
    ///
    /// <para><b>Both readers, not one.</b> The stranded SENDER is in another process as often as
    /// not and can see no line of this hub's log at all, so the same clause goes into the NACK it
    /// receives.</para>
    /// </summary>
    [Fact]
    public async Task ADiscardedDeferredDelivery_NamesTheTeardownThatDiscardedIt()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            AttributedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("attributed-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(AttributedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        // A routed DisposeRequest that STATES its reason — the shape every framework recycler
        // uses, and, since this change, the shape MeshOperations.Recycle uses too.
        host.Post(new DisposeRequest { Reason = TheStatedReason },
            o => o.WithTarget(AttributedAddress));

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        // THE SUBJECT, reader 1 — the stranded sender.
        failure.Failure!.Message.Should().Contain(host.Address.ToString(),
            "the NACK must name WHO asked for the teardown: the sender cannot see this hub's log, "
            + "so an answer that says only 'the hub went away' sends it to the wrong layer");
        failure.Failure.Message.Should().Contain(TheStatedReason,
            "and WHY, as the poster stated it — that sentence is the whole reason "
            + "DisposeRequest.Reason exists");

        // THE SUBJECT, reader 2 — the incident the red-log pipeline files.
        log.At(LogLevel.Error).Should().Contain(
            m => m.Contains(TheStatedReason, StringComparison.Ordinal)
                 && m.Contains(host.Address.ToString(), StringComparison.Ordinal),
            "event 7301 is the line that BECOMES the issue, and it told the reader to find a cause "
            + "the same hub had already recorded");

        // POSITIVE CONTROL — the gate name is still there. A rewrite that swapped one fact for
        // another would otherwise read as a fix (#3789 put the gate names on this line).
        log.At(LogLevel.Error).Should().Contain(
            m => m.Contains("attributed-gate-never-opens", StringComparison.Ordinal),
            "the attribution is ADDED to the report, never traded against the gate names");
    }

    /// <summary>
    /// The control arm, and the reason the assertion above cannot be satisfied by a constant: a hub
    /// taken down by a DIRECT <c>Dispose()</c> — host teardown, an owner's cascade, a <c>using</c> —
    /// has no routed request to attribute, and that ABSENCE is itself the answer. It rules the
    /// message path out, which is exactly what a reader of the production incident needed and could
    /// not get. A rendering that printed a blank here, or that printed the same clause as the arm
    /// above, would fail this.
    /// </summary>
    [Fact]
    public async Task ADiscardWithNoRoutedRequest_SaysSO_RatherThanSayingNothing()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            UnattributedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("unattributed-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(UnattributedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        // No routed DisposeRequest at all — the other half of the discriminator.
        gated!.Dispose();

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        failure.Failure!.Message.Should().Contain("a direct Dispose()",
            "no routed DisposeRequest brought this hub down, and saying so is what rules the "
            + "message path out — the attribution must VARY with the cause, or it is decoration");
        failure.Failure.Message.Should().NotContain(TheStatedReason,
            "the negative control: if the clause were a constant, the arm above would pass having "
            + "measured nothing");
        failure.Failure.Message.Should().Contain("reason not stated by the caller",
            "a poster that said nothing is reported as having said nothing — an absent answer that "
            + "renders as a blank reads to the next person as 'there was nothing to report'");
    }

    private static readonly Address BlankReasonAddress = new("blank-reason", "1");

    /// <summary>
    /// The third state, and the one a `null` check misses. <c>DisposeRequest.Reason</c> is FREE TEXT
    /// written by the poster, and only <c>null</c> was ever treated as "not stated" — so a poster
    /// that supplied an empty or whitespace string rendered a literal <c>why: </c> with nothing
    /// after it.
    ///
    /// <para>That is this page's own defect in a new costume: an absent answer that renders as
    /// nothing reads to the next person as "there was nothing to report". The normalisation lives
    /// at the single capture point, so every reader of the field inherits it rather than each
    /// having to remember — and this test is what stops the next reader adding one that does not.</para>
    /// </summary>
    [Fact]
    public async Task ATeardownWhoseReasonIsBlank_ReportsItAsUNSTATED_NotAsNothing()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            BlankReasonAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("blank-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(BlankReasonAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        // Whitespace, not empty string: an empty one is the obvious case and a `Length > 0` check
        // would pass it. Whitespace is what a poster writing $"{maybeEmpty} " actually produces.
        host.Post(new DisposeRequest { Reason = "   " },
            o => o.WithTarget(BlankReasonAddress));

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);

        failure.Failure!.Message.Should().Contain("reason not stated by the caller",
            "a poster that supplied only whitespace stated nothing, and 'stated nothing' is a "
            + "NAMED answer — the whole subject of this change is that an unset value must never "
            + "render the same as an absent one");
        failure.Failure.Message.Should().NotContain("why:  ",
            "the negative control: the defect is a literal 'why: ' with nothing after it, which is "
            + "exactly what a null-only check produces and what a reader takes for a blank field");

        // POSITIVE CONTROL — the WHO half is still there, so 'the reason came out named' cannot be
        // satisfied by the attribution having been dropped altogether.
        failure.Failure.Message.Should().Contain(host.Address.ToString(),
            "normalising the reason must not cost the sender the attribution it travels with");
    }

    private static readonly Address RecognisedAddress = new("recognised", "1");

    /// <summary>
    /// 🚨 Systemorph/MeshWeaver#4866 — the discard NACK told the sender to <i>"retry to get the
    /// authoritative answer"</i> in a sentence no reader could tell was retryable.
    ///
    /// <para><b>The defect.</b> This site hand-wrote its refusal (<c>"Hub X was disposed
    /// while …"</c>) instead of composing it through <see cref="ShutdownNack"/>, so it opened with
    /// no <see cref="ShutdownNack.Banner"/>. Three independent classifiers decide retryability by
    /// MESSAGE TEXT rather than by <see cref="ErrorType"/> —
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>,
    /// <c>AreaErrorClassifier.IsTransientHubFailure</c>/<c>IsHubRecycling</c> and
    /// <c>OrleansRoutingService.ClassifyRoutedFailure</c> — and none of their markers appears in
    /// that sentence. So the transient <see cref="ErrorType.ShuttingDown"/> the test above pins was
    /// carried by an envelope the consumers do not read, and every consumer with recovery
    /// machinery took the corpse's answer as FINAL: the cache did not re-probe, the layout area did
    /// not re-arm, and the accepted work was genuinely lost rather than re-asked. That is the
    /// difference between the 105 discards of #4866 being retryable noise and being 105 requests
    /// nobody ever answers.</para>
    ///
    /// <para><b>Why this is not the ErrorType assertion again.</b>
    /// <see cref="ShutdownNack.IsAnsweredByOwner"/> is deliberately NOT an ErrorType test — the
    /// routing layer mints that same classification off the same text, so an ErrorType test answers
    /// the question with the routing layer's echo of it. The banner is the only evidence that
    /// identifies the speaker, and the drain was the one producer in this service not emitting it.
    /// <c>OwnerAnswerRecognitionGuard</c> pins the same contract over every producer's sentence;
    /// this test is the LIVE arm, over the delivery the production path actually discards.</para>
    /// </summary>
    [Fact]
    public async Task ADiscardedDeferredDelivery_IsRecognisedAsTheOwnersAnswer()
    {
        var host = GetHost();

        var gated = host.GetHostedHub(
            RecognisedAddress,
            c => c.WithTypes(typeof(GatedRequest), typeof(GatedResponse))
                .WithInitializationGate("recognised-gate-never-opens", _ => false)
                .WithHandler<GatedRequest>((h, d) =>
                {
                    h.Post(new GatedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        gated.Should().NotBeNull();

        var response = host
            .Observe<GatedResponse>(new GatedRequest(), o => o.WithTarget(RecognisedAddress))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        await WaitForDeferredBacklog(host);

        host.Post(new DisposeRequest { Reason = TheStatedReason },
            o => o.WithTarget(RecognisedAddress));

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        failure.Failure.Should().NotBeNull();
        Output.WriteLine(failure.Failure!.Message ?? "(no message)");

        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, RecognisedAddress).Should().BeTrue(
            "the hub that discarded the delivery IS the speaker, and every consumer that decides "
            + "'retry the fresh activation' vs 'take this as final' reads the banner to know it — "
            + "compose the refusal through ShutdownNack rather than widening the predicate");
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, RecognisedAddress.Path)
            .Should().BeTrue(
                "a reader that resolved the node by PATH holds nothing else, and the two halves "
                + "must answer identically or recognition is a coin toss on which one is at hand");

        ShutdownNack.ExtractActivationTag(failure.Failure.Message).Should().NotBeNullOrEmpty(
            "a consumer re-probing this address cannot otherwise tell ONE hub wedged in teardown "
            + "from a recycle storm — those have opposite fixes (#2025), and every ShuttingDown "
            + "NACK minted for a delivery the reader can re-probe carries the activation tag");

        // 🚨 …and it is the RIGHT tag. #2376's review found the drift in BOTH directions: one site
        // embedded no identity at all, and another paired the marker with a per-DELIVERY id — which
        // varies on every retry against the SAME activation, so it defeats the distinct-activation
        // counter exactly as an absent tag does while being perfectly non-empty. The assertion
        // above cannot see that one. FormatActivationTag keys on REFERENCE identity, so comparing
        // against the hub instance is exact.
        failure.Failure.Message.Should().Contain(ShutdownNack.FormatActivationTag(gated!),
            "the tag must identify THIS activation — a future edit that puts delivery.Id where the "
            + "activation marker belongs passes the not-empty check above and still leaves a "
            + "re-probe rider counting a fresh activation on every retry (#2376)");

        // NEGATIVE CONTROL — the predicate must still be able to say no, or the assertions above
        // are satisfied by anything that mentions an address.
        ShutdownNack.IsAnsweredByOwner(failure.Failure.Message, new Address("recognised", "2"))
            .Should().BeFalse(
                "a DIFFERENT address is not this owner answering; if this passes the refusal is "
                + "being recognised by something other than its own banner");

        // POSITIVE CONTROLS — the facts the refusal already carried are ADDED to, never traded
        // against, which is how a rewrite of this sentence reads as a fix while losing evidence.
        failure.Failure.Message.Should().Contain("recognised-gate-never-opens",
            "the gate that actually held the delivery (#3712/#3789) survives the rewording");
        failure.Failure.Message.Should().Contain(TheStatedReason,
            "and so does WHY the teardown happened, as its poster stated it");
        failure.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the envelope was never the broken half — it must stay transient too");
    }

    /// <summary>
    /// Polls the public disposal diagnostics (which report <c>deferred=&lt;N&gt;</c> per hub,
    /// walking hosted hubs) until something is parked. The gated hub is the only hub in this test
    /// that defers, so a non-zero count is unambiguously our request.
    /// </summary>
    private static async Task WaitForDeferredBacklog(IMessageHub host)
    {
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => host.GetDisposalDiagnostics())
            .Where(snapshot => Regex.Matches(snapshot, @"deferred=(\d+)")
                .Any(m => int.Parse(m.Groups[1].Value) > 0))
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }
    private sealed class DeferredLog : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

        /// <summary>Event 7301 messages, any level — what the original assertions read.</summary>
        public IEnumerable<string> Messages => Lines.Select(l => l.Message);

        public IEnumerable<string> At(LogLevel level) =>
            Lines.Where(l => l.Level == level).Select(l => l.Message).ToArray();

        public ILogger CreateLogger(string categoryName) => new Capture(Lines);
        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<(LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == 7301)
                    lines.Enqueue((level, formatter(state, exception)));
            }
        }
    }

}
