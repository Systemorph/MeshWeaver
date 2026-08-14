using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

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
    /// liveness check there), so the read is guaranteed to be outstanding. The teardown is then
    /// gated on the framework's OWN record that the handler ran — the request-fate ledger reaching
    /// <c>HANDLER_EXIT</c> — so the disposal cannot race ahead of the handler and let a routing
    /// NACK stand in for the arm under test. The message assertion pins that arm specifically.</para>
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
            .ToTask(TestContext.Current.CancellationToken);

        await WaitUntilTheHandlerHasRunAndAnsweredNothing(reader);
        Output.WriteLine("[TEST] handler ran and answered nothing — now recycling the owner");

        Mesh.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));

        var result = await answer;
        Output.WriteLine($"[TEST] answer: {result}");

        result.Should().BeOfType<DeliveryFailureException>(
            "a read whose owner is torn down mid-flight must be NACKed, not left hanging — the "
            + "caller cannot distinguish 'still working' from 'will never answer'");
        var failure = ((DeliveryFailureException)result!).Failure;
        failure.Should().NotBeNull();
        failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the owner is going away — this is retry-worthy, NOT an absence");
        failure.Message.Should().Contain("still outstanding",
            "this pins HandleGetDataRequest's DISPOSAL arm specifically; the routing-level NACK "
            + "for an abandoned delivery carries different wording, so a passing assertion here "
            + "cannot be satisfied by the pre-existing path instead of the one under test");
        failure.Message.Should().Contain("shutting down",
            "MeshNodeStreamCache.IsTransientOwnerFailure classifies by this marker; without it a "
            + "long-lived stream consumer tears down instead of riding the recycle out");
        failure.Message.Should().NotContain("No node found",
            "that phrase turns a retryable stall into a PROVABLE absence (MeshNodeStreamCache"
            + ".IsMissingNodeFailure) — the exact confusion this NACK exists to avoid");
    }

    /// <summary>
    /// The CONSUMER-visible outcome, and the bound on the re-probe in one test: a caller using
    /// <c>GetMeshNode</c> against an owner that is torn down mid-read <b>gets its node</b>, quickly.
    ///
    /// <para>This is what the fix is FOR. Before it the same sequence produced
    /// <c>GetMeshNode('…') timed out after 60.0s … Target: NO LOCAL HUB</c> — a minute of stall
    /// ending in a wrong explanation. Now the NACK arrives, <c>GetMeshNode</c> re-probes ONCE
    /// against the fresh activation, and the read resolves.</para>
    ///
    /// <para>🚨 It also bounds the re-probe by demonstration rather than by assertion, which
    /// matters because a transient NACK a consumer answers by asking again is the shape behind the
    /// 2026-06-08 resubscribe-storm outage. The claim is <c>Interlocked.Exchange(ref reProbed, 1)</c>
    /// on a local declared INSIDE <c>GetMeshNode</c>'s <c>Observable.Create</c> — one per
    /// subscription, no timer, no <c>Retry</c> operator, no shared counter. A loop would never
    /// settle on a value; this settles on the real node, far inside the budget.</para>
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
            .ToTask(TestContext.Current.CancellationToken);

        // Same ordering gate as above — the read must be genuinely outstanding at a hub that has
        // already handled it before the teardown starts.
        await WaitUntilTheHandlerHasRunAndAnsweredNothing(reader);

        Mesh.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));

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
            .ToTask(TestContext.Current.CancellationToken);

        var result = await probe;
        Output.WriteLine($"[TEST] answer within 5s: {result?.ToString() ?? "<none — correct>"}");

        result.Should().BeNull(
            "a healthy hub must never be reported as shutting down. Whatever this read's source "
            + "did, the owner is at Started, so there is no truthful terminal to send — and a "
            + "false one both lies and can beat a correct answer from another handler for the "
            + "same request (the layoutAreas regression)");
    }

    /// <summary>
    /// Blocks until the framework's OWN record shows the owner's handler ran to completion with no
    /// reply — the state both teardown tests must reach before they may recycle the owner.
    ///
    /// <para>🚨 <b>The gate this replaces could not fail.</b> It asked for two things at
    /// <c>t = 0</c>: that the reader holds a pending <c>GetDataRequest@</c> callback (true the
    /// instant the read is subscribed, before anything is routed) and that the OWNER's queue reads
    /// <c>Queue(buffer=0,deferred=0,exec=0)</c> — which is what an idle hub looks like BEFORE the
    /// delivery ever arrives. Both were satisfied by a request still in flight, so the
    /// <c>DisposeRequest</c> was free to overtake it. Its own comment claimed the gate was "the
    /// request-fate ledger reaching <c>HANDLER_EXIT</c>"; the code never looked at the ledger. That
    /// is what made <c>OwnerDisposedWithReadOutstanding_IsNacked_NotLeftHanging</c> red on main —
    /// the read landed mid-teardown and exercised a DIFFERENT arm than the one asserted (#1470).</para>
    ///
    /// <para>The ledger IS available, just not through <c>GetPendingRequestDiagnostics</c>: it is
    /// per hub TREE (a hosted hub shares its root's), and <c>GetDisposalDiagnostics</c> renders the
    /// handler-side trail for every pending callback. So <c>HANDLER_EXIT</c> appearing there is the
    /// owner's own record that it entered and left the handler — and the callback still being
    /// pending is the record that it answered nothing. Polling a public snapshot for a specific,
    /// positive fact is the sanctioned wait shape; it fails loudly instead of proceeding.</para>
    /// </summary>
    private static Task WaitUntilTheHandlerHasRunAndAnsweredNothing(IMessageHub reader) =>
        Observable.Interval(TimeSpan.FromMilliseconds(25)).StartWith(0L)
            .Select(_ => reader.GetDisposalDiagnostics())
            .Where(diagnostics =>
                diagnostics.Contains("GetDataRequest", StringComparison.Ordinal)
                && diagnostics.Contains("HANDLER_EXIT", StringComparison.Ordinal))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask(TestContext.Current.CancellationToken);
}
