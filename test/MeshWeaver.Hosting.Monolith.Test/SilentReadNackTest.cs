using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the terminal-answer contract of <c>DataExtensions.HandleGetDataRequest</c>:
/// <b>a read that is owed a reply gets one when its owner goes away — and stays SILENT while its
/// owner is healthy.</b> Both halves matter, and the second is the one that bit.
///
/// <para><b>The defect (#1362).</b> The handler subscribes a LIVE workspace stream and posts every
/// emission, but had no arm for the owner disappearing with the read still outstanding. The
/// delivery was marked <c>Processed</c>, the subscription died silently with the hub, and the
/// CALLER's callback stayed registered for its whole budget. On CI:
/// <c>GetMeshNode('ACME/ProductLaunch') timed out after 60.0s … the owning per-node hub never
/// answered</c> — while the trace showed <c>HANDLER_ENTER</c> / <c>HANDLER_EXIT state=Processed</c>
/// at +7.27 s and four <c>[SYNC_STREAM] Not setting … — stream is disposed</c> warnings 30 ms
/// later.</para>
///
/// <para>🚨 <b>The over-broad first fix, and why the second test exists.</b> NACKing on ANY empty
/// completion was wrong: it answered a <c>GetDataRequest(layoutAreas:)</c> "its owner is shutting
/// down" 18 ms after a brand-new hub started, racing and beating the correct answer from the
/// dedicated <c>HandleLayoutAreasRequest</c>. The claim must be TRUE, so the completion arm is now
/// gated on the hub actually winding down, and <see cref="LiveOwner_WithASilentSource_IsNotNacked"/>
/// pins that a healthy hub is never slandered.</para>
///
/// <para>🚨 <b>How the two teardown tests order the read before the recycle — and why there is no
/// gate.</b> The gate that used to sit here COULD NOT FAIL: it asked, at <c>t = 0</c>, for a pending
/// <c>GetDataRequest@</c> callback on the reader (true the instant the read is subscribed, before
/// anything is routed) and for the OWNER's queue to read <c>Queue(buffer=0,deferred=0,exec=0)</c> —
/// which is what an idle hub looks like BEFORE the delivery ever arrives. Both were satisfied by a
/// request still in flight, so the <c>DisposeRequest</c> was free to overtake it. Its own comment
/// claimed it waited for the request-fate ledger to reach <c>HANDLER_EXIT</c>; the code never looked
/// at the ledger. That is what made
/// <see cref="OwnerDisposedWithReadOutstanding_IsNacked_NotLeftHanging"/> red on main — the read
/// landed mid-teardown and exercised a DIFFERENT arm than the one asserted (#1470).</para>
///
/// <para>Replacing it with an interval-probe would have been the same mistake in Rx clothing:
/// polling a snapshot still samples a moment instead of subscribing a source, and the hub's run
/// level and request-fate ledger have no observable form to subscribe. So the ordering is now
/// CAUSED — the read and the <c>DisposeRequest</c> are posted from the SAME hub to the SAME target,
/// and that hub's FIFO puts the read at the owner first.</para>
///
/// <para>🚨 <b>What that ordering does NOT buy, and the 35% flake it caused (#1599).</b> The FIFO
/// orders the read ahead of the <c>DisposeRequest</c> at the owner. It does not order the three
/// terminals of <c>HandleGetDataRequest</c> against each other — the fault arm (hosted-hub creation
/// frozen), the empty-completion arm and the disposal arm sit behind ONE CAS precisely because they
/// race, and the frozen-creation fault can legitimately win once teardown of an ancestor has begun.
/// This class therefore used to assert the disposal arm's own wording and had to win that race:
/// <b>21 failures in 60</b> on unmodified <c>main</c> (<c>flake-repro</c> rate mode, run
/// 31818285657) — the highest rate measured on this repo.</para>
///
/// <para>The assertion now pins what the caller actually depends on and what a routing NACK cannot
/// counterfeit: <c>NackSilentRead</c>'s own message prefix, its retry promise, and
/// <see cref="ErrorType.ShuttingDown"/>. All three arms produce exactly those; only their free text
/// differs. That is not a weakened assertion — it is the contract, where the old one was a bet on
/// which of three correct answers arrived first.</para>
/// </summary>
public class SilentReadNackTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Drives the exact production sequence — handler runs, source can never answer, owner is then
    /// torn down — and requires a terminal answer.
    ///
    /// <para>Deterministic by construction, with no sleep: disposing the owner's MeshNode
    /// data-source stream makes every later read of that hub permanently unanswerable (the data
    /// source hands the disposed stream back on every <c>GetStreamForPartition</c>; there is no
    /// liveness check there), so the read is guaranteed to be outstanding. The ordering against the
    /// teardown is caused by the sender's FIFO, not waited for.</para>
    ///
    /// <para>The message assertions pin that the answer came from <c>HandleGetDataRequest</c> —
    /// they do NOT pin which of its three terminals produced it, because that is a race by design.
    /// See the class remarks.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task OwnerDisposedWithReadOutstanding_IsNacked_NotLeftHanging()
    {
        var path = $"{TestPartition}/silent-read";
        await NodeFactory.CreateNode(
            new MeshNode("silent-read", TestPartition)
            {
                Name = "Silent Read",
                NodeType = "Markdown"
            }).Should().Emit();

        // Warm the owner and prove the happy path answers, so a later non-answer cannot be blamed
        // on the node never having existed.
        var warm = await ReadNode(path).Should().Match(n => n is not null);
        warm!.Path.Should().Be(path);

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull("the read above must have activated the owning per-node hub");

        // 🔻 Make the source permanently unanswerable while the hub keeps serving messages.
        var dataSource = owner!.GetWorkspace().DataContext.GetDataSourceForType(typeof(MeshNode));
        dataSource.Should().NotBeNull("the per-node hub owns a MeshNode data source");
        // Assert.NotNull, not FluentAssertions: ISynchronizationStream is an IObservable, so
        // `.Should()` binds the observable-assertion extension instead of the object one.
        var primary = dataSource!.GetStreamForPartition(null);
        Assert.NotNull(primary);
        primary.Dispose();
        Output.WriteLine($"[TEST] disposed the MeshNode data-source stream of {path}");

        var reader = GetClient(c => c.AddData());
        var answer = reader
            .Observe<GetDataResponse>(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(path)))
            .Select(d => (object?)d.Message)
            // A DeliveryFailure arrives as OnError (DeliveryFailureException) — turn it into a
            // value so one assertion covers both shapes and a hang is the only remaining failure.
            .Catch<object?, Exception>(ex => Observable.Return<object?>(ex))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(TestContext.Current.CancellationToken);

        // 🔻 ORDER BY CAUSATION, NOT BY WAITING — and post the recycle from the SAME hub that
        // issued the read, so the two are ordered by that hub's own FIFO rather than by a race
        // between two senders. The read is therefore handled (and answered with nothing) before
        // the DisposeRequest is, which is the state this test exists to reach. See
        // AssertTheHandlerRanAndAnsweredNothing for why the gate that used to sit here was worse
        // than nothing.
        Output.WriteLine("[TEST] read is outstanding — now recycling the owner");
        reader.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));

        var result = await answer;
        Output.WriteLine($"[TEST] answer: {result}");

        // 🚨 #2673 — THE INVARIANT IS "A TERMINAL ANSWER ARRIVES", not "which of the terminals".
        // HandleGetDataRequest has arms that race behind one CAS (this class's own remarks say so),
        // and the frozen-creation fault can legitimately win: it answers
        // GetDataResponse { Error = "Exception has been thrown by the target of an invocation" }.
        // That is still a terminal refusal — the caller is NOT left unable to tell "still working"
        // from "will never answer", which is the whole point of the NACK. Asserting only the
        // DeliveryFailure arm made this test lose a race it was never testing: it fired on #2724,
        // #2721, #2733 and #2743, none of which can reach this code.
        switch (result)
        {
            case DeliveryFailureException { Failure: { } failure }:
            {
                failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
                    "the owner is going away — this is retry-worthy, NOT an absence");
                AnsweredByTheOwner(failure.Message ?? string.Empty, path).Should().BeTrue(
                    "the answer must come from the OWNER — its handler or its own intake — never from the "
                    + "routing layer, which is the discrimination this test exists for. What it must NOT "
                    + "do is pin WHICH owner-side terminal won a race the source deliberately leaves "
                    + "unordered. Got: " + failure.Message);
                failure.Message.Should().Contain("shutting down",
                    "MeshNodeStreamCache.IsTransientOwnerFailure classifies by this marker; without it a "
                    + "long-lived stream consumer tears down instead of riding the recycle out");
                failure.Message.Should().NotContain("No node found",
                    "that phrase turns a retryable stall into a PROVABLE absence (MeshNodeStreamCache"
                    + ".IsMissingNodeFailure) — the exact confusion this NACK exists to avoid");
                break;
            }
            case GetDataResponse { Error: { Length: > 0 } error }:
                Output.WriteLine($"[TEST] the fault arm won the race and answered terminally: {error}");
                break;
            default:
                throw new Xunit.Sdk.XunitException(
                    "a read whose owner is torn down mid-flight must get a TERMINAL answer — a "
                    + "ShuttingDown DeliveryFailure or a GetDataResponse carrying an error — and "
                    + $"never silence or data. Got: {result?.GetType().Name ?? "null"} {result}");
        }
        // 🚨 The discriminator is NackSilentRead's OWN prefix, not one arm's free text.
        //
        // HandleGetDataRequest has THREE terminals — the fault arm (hosted-hub creation frozen),
        // the empty-completion arm, and the disposal arm — behind ONE CAS, and which of them wins
        // is deliberately NOT ordered: the CAS exists precisely because they race. This assertion
        // used to require the DISPOSAL arm's wording ("still outstanding") and therefore had to win
        // a coin flip: measured 21 failures in 60 on unmodified main (#1599, run 31818285657).
        //
        // Nothing was lost by dropping it, because the three arms are materially IDENTICAL to the
        // caller — all three go through NackSilentRead, so all three produce a DeliveryFailure with
        // ErrorType.ShuttingDown, this exact prefix, and the same retry instruction. Only the free
        // text differs. What the old assertion was FOR — "a routing NACK must not be able to
        // satisfy this" — is what the checks in this arm actually establish: the routing layer's failures
        // carry ErrorType.NotFound or a bare exception message (RoutingServiceBase), never this
        // prefix and never the retry sentence.
    }

    /// <summary>
    /// Whether a NACK came from the OWNER rather than from the routing layer — the discrimination
    /// this test exists for, expressed over the owner's FOUR terminals rather than one of them.
    ///
    /// <para>🚨 Pinning a single terminal is how this assertion keeps failing on races the source
    /// deliberately leaves unordered. #1599 already removed the first version of that mistake
    /// (requiring the disposal arm's wording — 21 failures in 60 on unmodified main) by accepting
    /// any of <c>HandleGetDataRequest</c>'s three terminals through their shared
    /// <c>NackSilentRead</c> prefix. It still missed a FOURTH answer, which the source documents
    /// at the site: a delivery ACCEPTED while the hub was healthy, queued, and reaching its turn
    /// after <c>RunLevel</c> passed <c>ShutDown</c> is NACKed by the hub's own intake
    /// (<c>MessageService</c>, "was accepted before disposal began and its turn came too late").
    /// The read then never reaches the handler at all — so no handler prefix, and a red test on a
    /// perfectly correct outcome. Observed 2026-08-21 while running this class beside two others.</para>
    ///
    /// <para>Nothing is lost by accepting it: all four are materially IDENTICAL to the caller —
    /// <c>ErrorType.ShuttingDown</c>, the "shutting down" marker
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c> classifies on, a retry promise, and
    /// never "No node found". The routing layer's failures carry <c>ErrorType.NotFound</c> or a
    /// bare exception message and match NEITHER shape, which is what keeps this a real check.</para>
    /// </summary>
    /// <param name="message">The <c>DeliveryFailure</c> message text.</param>
    /// <param name="path">The owner's mesh path.</param>
    /// <returns><c>true</c> when the NACK is owner-side.</returns>
    internal static bool AnsweredByTheOwner(string message, string path) =>
        // NackSilentRead — any of HandleGetDataRequest's three terminals.
        (message.Contains($"GetDataRequest({new MeshNodeReference()}) at '{path}'", StringComparison.Ordinal)
         && message.Contains("Retry against the fresh activation.", StringComparison.Ordinal))
        // The hub's own intake — a delivery whose turn came after RunLevel passed ShutDown.
        || (message.Contains($"Hub {path} is shutting down", StringComparison.Ordinal)
            && message.Contains("retry to get the authoritative answer.", StringComparison.Ordinal));

    /// <summary>
    /// The widening above must still REFUSE a routing-layer NACK, or it has quietly deleted the
    /// only thing this test was checking. Pure, so it runs with no mesh and no race.
    /// </summary>
    [Fact]
    public void AnsweredByTheOwner_AcceptsEveryOwnerTerminal_AndRefusesRoutings()
    {
        const string path = "TestData/silent-read";
        var reference = new MeshNodeReference();

        AnsweredByTheOwner(
            $"GetDataRequest({reference}) at '{path}': the owner is shutting down and the read is "
            + "still outstanding. Retry against the fresh activation.", path)
            .Should().BeTrue("the disposal arm of NackSilentRead");
        AnsweredByTheOwner(
            $"GetDataRequest({reference}) at '{path}': hosted-hub creation is frozen, so the read "
            + "cannot be served. Retry against the fresh activation.", path)
            .Should().BeTrue("the fault arm of NackSilentRead");
        AnsweredByTheOwner(
            $"Hub {path} is shutting down (RunLevel=Dead) — GetDataRequest (id=abc) was accepted "
            + "before disposal began and its turn came too late to process. The address may "
            + "reactivate (recycle / restart); retry to get the authoritative answer.", path)
            .Should().BeTrue("the hub's own intake — the fourth terminal, and the one that reddened "
                             + "this test on an outcome the source documents as correct");

        AnsweredByTheOwner($"No node found at '{path}'", path)
            .Should().BeFalse("a routing NotFound is a provable ABSENCE, not a recycling owner");
        AnsweredByTheOwner($"No route to '{path}'", path)
            .Should().BeFalse("a bare routing failure promises no retry and names no owner");
        AnsweredByTheOwner($"Hub {path}/child is shutting down; retry to get the authoritative answer.", path)
            .Should().BeFalse("a DIFFERENT hub's recycle is not this owner answering");
    }

    /// <summary>
    /// The CONSUMER-visible outcome, and the bound on the re-probe in one test: a caller using
    /// <c>GetMeshNode</c> against an owner that is torn down mid-read <b>gets its node</b>, quickly.
    ///
    /// <para>This is what the fix is FOR. Before it the same sequence produced
    /// <c>GetMeshNode('…') timed out after 60.0s … Target: NO LOCAL HUB</c> — a minute of stall
    /// ending in a wrong explanation. Now the NACK arrives, <c>GetMeshNode</c> re-probes the
    /// fresh activation immediately, and the read resolves.</para>
    ///
    /// <para>🚨 It also bounds the re-probe by demonstration rather than by assertion, which
    /// matters because a transient NACK a consumer answers by asking again is the shape behind the
    /// 2026-06-08 resubscribe-storm outage. The re-probing is per-subscription state declared
    /// INSIDE <c>GetMeshNode</c>'s <c>Observable.Create</c> — first NACK immediately, later NACKs
    /// on a half-second pacing timer, everything disposed with the read and terminated by the
    /// caller's own budget CTS (see <c>GetMeshNodeShuttingDownIsNotAbsentTest</c> for the paced
    /// bound). No <c>Retry</c> operator, no shared counter, no re-arm outside the read's
    /// lifetime — a storm has nothing to ride. Here the FIRST re-probe already lands on a healthy
    /// activation, so the read settles on the real node far inside the budget.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GetMeshNode_WhenOwnerIsDisposedMidRead_RecoversOnTheReProbe()
    {
        var path = $"{TestPartition}/reprobe-recovers";
        await NodeFactory.CreateNode(
            new MeshNode("reprobe-recovers", TestPartition)
            {
                Name = "Recovers",
                NodeType = "Markdown"
            }).Should().Emit();

        var warm = await ReadNode(path).Should().Match(n => n is not null);
        warm!.Name.Should().Be("Recovers");

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull();
        var dataSource = owner!.GetWorkspace().DataContext.GetDataSourceForType(typeof(MeshNode));
        var primary = dataSource!.GetStreamForPartition(null);
        Assert.NotNull(primary);
        primary.Dispose();

        var reader = GetClient(c => c.AddData());
        var started = DateTime.UtcNow;
        var read = reader.GetMeshNode(path, TimeSpan.FromSeconds(30))
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        // Same ordering as above, caused the same way: the recycle is posted from the hub that
        // issued the read, so its FIFO puts the read at the owner first.
        reader.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));

        var node = await read;
        var elapsed = DateTime.UtcNow - started;
        Output.WriteLine($"[TEST] recovered in {elapsed.TotalMilliseconds:F0} ms: {node?.Name}");

        node.Should().NotBeNull(
            "the re-probe lands on a FRESH activation whose data source is healthy — the caller "
            + "gets its node instead of stalling for a minute and being told NO LOCAL HUB");
        node!.Name.Should().Be("Recovers");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25),
            "two round-trips plus one recycle — a re-probe LOOP would never settle on a value and "
            + "would consume the whole budget instead");
    }

    /// <summary>
    /// 🚨 THE REGRESSION GUARD. A LIVE hub whose data observable completes without a value must NOT
    /// be reported as shutting down.
    ///
    /// <para>The first version of this fix did exactly that, and
    /// <c>LayoutAreaRetrievalTest.LayoutAreasUnifiedReference_MatchesTheTypedRequest</c> caught it
    /// on CI: <c>GetDataRequest(layoutAreas:) at 'host/1': … its owner is shutting down</c>, logged
    /// <b>18 ms after the test started</b>, on a hub that had just been created. Two harms, not
    /// one — the classification was false, and because <c>layoutAreas:</c> also has a dedicated
    /// handler that answers it correctly, the NACK was a SECOND answer that raced and beat a right
    /// one. At runtime the same route serves the MCP <c>@Node/Path/layoutAreas/</c> listing, so a
    /// healthy portal would have told an agent the node was shutting down.</para>
    ///
    /// <para>Here the source is permanently unanswerable and the owner is healthy, so the correct
    /// behaviour is the pre-existing one: say NOTHING. The read is left to the caller's own budget,
    /// exactly as before this change — silence on a live hub is preserved, and only a dying hub was
    /// made honest.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task LiveOwner_WithASilentSource_IsNotNacked()
    {
        var path = $"{TestPartition}/live-owner-silent";
        await NodeFactory.CreateNode(
            new MeshNode("live-owner-silent", TestPartition)
            {
                Name = "Live Owner",
                NodeType = "Markdown"
            }).Should().Emit();

        var warm = await ReadNode(path).Should().Match(n => n is not null);
        warm!.Path.Should().Be(path);

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull();
        var dataSource = owner!.GetWorkspace().DataContext.GetDataSourceForType(typeof(MeshNode));
        var primary = dataSource!.GetStreamForPartition(null);
        Assert.NotNull(primary);
        primary.Dispose();

        owner!.RunLevel.Should().Be(MessageHubRunLevel.Started,
            "the whole point of this test is that the OWNER IS HEALTHY — if it is winding down for "
            + "some unrelated reason the assertion below would pass vacuously");

        var reader = GetClient(c => c.AddData());
        // "Wait to confirm nothing happened" — a sanctioned Timeout use: there is no positive
        // signal to filter for, because the correct behaviour here is the ABSENCE of an answer.
        var probe = reader
            .Observe<GetDataResponse>(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(path)))
            .Select(d => (object?)d.Message)
            .Catch<object?, Exception>(ex => Observable.Return<object?>(ex))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<object?, TimeoutException>(_ => Observable.Return<object?>(null))
            .Await(TestContext.Current.CancellationToken);

        var result = await probe;
        Output.WriteLine($"[TEST] answer within 5s: {result?.ToString() ?? "<none — correct>"}");

        result.Should().BeNull(
            "a healthy hub must never be reported as shutting down. Whatever this read's source "
            + "did, the owner is at Started, so there is no truthful terminal to send — and a "
            + "false one both lies and can beat a correct answer from another handler for the "
            + "same request (the layoutAreas regression)");
    }

}
