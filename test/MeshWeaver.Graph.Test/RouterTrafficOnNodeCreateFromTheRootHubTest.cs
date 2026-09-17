using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The discriminating instrument for
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/1140">#1140</see></b> — the production
/// <c>ROUTER_TRAFFIC</c> pair that has fired 41,087 times on <c>memex</c> between 2026-08-10 and
/// 2026-09-12, every recent sample naming the log-incident ingest:
///
/// <code>
/// RawJson has the mesh hub as sender (sender: mesh/6CuC…, target: Admin/_LogIncident/cb33ad…)
/// CreateNodeResponse has the mesh hub as target (sender: Admin/_LogIncident/cb33ad…, target: mesh/6CuC…)
/// </code>
///
/// <para><b>What the production samples cannot say.</b> The detector fires on the RECEIVING hub, so
/// the line carries the delivery's two ends and no call site. Two candidates produce exactly that
/// pair and the log cannot separate them: <c>MeshService.CreateNode</c> issuing on
/// <c>NodeOperationIssuingHub()</c> — which is <c>NodeOperationExecutionHub() ?? hub</c>, i.e. the
/// ROOT hub whenever the execution hub cannot be materialised — or the post-creation
/// <c>DataChangeRequest</c> the create path posts for a handler's additional nodes. This fixture
/// drives the FIRST candidate through a real mesh and reads what the detector actually logged, so
/// the answer is measured rather than picked.</para>
///
/// <para><b>The shape is production's, not a stand-in.</b> <c>LogIncidentIngestService</c> is a
/// mesh-singleton that takes the DI-injected <see cref="IMessageHub"/> — which in the mesh's root
/// container IS the router — and calls
/// <c>hub.ServiceProvider.GetRequiredService&lt;IMeshService&gt;().CreateNode(node)</c>. That is
/// exactly what <see cref="ACreateIssuedFromTheRootMeshHub_NeverPutsTheRouterOnEitherEnd"/> does,
/// and it is the shape every mesh-singleton consumer of the seam has (the plugin-catalog boot
/// services, the content importers, the one-shot node reads).</para>
///
/// <para><b>Scope.</b> This covers the request/response exchange — candidate one. The post-creation
/// <c>DataChangeRequest</c> fires only for a post-creation handler that returns ADDITIONAL nodes,
/// which the probe's type has none of, so a green here narrows #1140 to that second candidate
/// rather than clearing the create path wholesale.</para>
///
/// <para><b>Both detector sites are read.</b> <c>MessageHub</c> reports the violation twice over:
/// once on the RECEIVING hub (<c>ROUTER_TRAFFIC:</c>, the historical line, which carries the two
/// addresses and no call site) and once at the ORIGIN (<c>ROUTER_TRAFFIC ORIGIN:</c>, which carries
/// the posting stack). The measurement requires silence from BOTH, so a violating post is caught
/// even where the receiving hub's own report is muted.</para>
///
/// <para><b>The negative control is not optional.</b> A green above is evidence only if this
/// fixture's capture CAN record a violation — see
/// <see cref="TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork"/>, which makes the
/// router an end on purpose and requires BOTH lines, the origin one naming this test's own frame.
/// Without it, "no ROUTER_TRAFFIC" and "the logger provider was never wired into this mesh" are the
/// same reading.</para>
/// </summary>
public class RouterTrafficOnNodeCreateFromTheRootHubTest : MonolithMeshTestBase
{
    /// <summary>The control's probe: a message with no meaning beyond being WORK the router posts.</summary>
    private record RouterOriginProbe;

    private readonly RouterTrafficCapture _capture = new();

    /// <summary>Producer → test: the client hub completes this when the probe reaches its handler.</summary>
    private readonly System.Reactive.Subjects.AsyncSubject<System.Reactive.Unit> _probeArrived = new();

    public RouterTrafficOnNodeCreateFromTheRootHubTest(ITestOutputHelper output) : base(output)
    {
        // Registered AFTER the base constructor's ClearProviders(), so it survives alongside the
        // xUnit sink. The detector's whole contract is the ERROR it emits; reading that record is
        // the only way to assert on it without re-implementing the decision here.
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(_capture));
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureHub(c => c.WithTypes(typeof(RouterOriginProbe)));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(RouterOriginProbe))
            .WithHandler<RouterOriginProbe>((_, delivery) =>
            {
                _probeArrived.OnNext(System.Reactive.Unit.Default);
                _probeArrived.OnCompleted();
                return delivery.Processed();
            });

    /// <summary>
    /// 🚨 THE MEASUREMENT. A node create issued the way every mesh-singleton service issues one —
    /// from the DI-injected root hub — must leave the router off BOTH ends of the exchange.
    ///
    /// <para>Red ⇒ <c>NodeOperationIssuingHub</c> is the live path behind #1140's production lines,
    /// and the captured record names the role it failed in. Green ⇒ the seam holds for the create
    /// exchange and the remaining candidate is the post-creation <c>DataChangeRequest</c>.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACreateIssuedFromTheRootMeshHub_NeverPutsTheRouterOnEitherEnd()
    {
        // The production shape verbatim: the service holds the root hub and resolves IMeshService
        // from ITS provider, so MeshService's injected IMessageHub is the router.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var created = await meshService
            .CreateNode(new MeshNode("RouterTrafficIngestProbe", TestPartition)
            {
                Name = "Router Traffic Ingest Probe",
                NodeType = "Markdown",
            })
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        created.Path.Should().Be($"{TestPartition}/RouterTrafficIngestProbe",
            "the create must actually have happened — a create that never ran emits no traffic at "
            + "all, which would make the assertion below vacuous");

        // Both halves of the production pair, asserted separately so a red says WHICH one fired.
        DumpReports();
        Reports().Where(r => r.Role.Contains("sender", StringComparison.Ordinal)).Should().BeEmpty(
            "#1140's first production line is the request reaching the node's own hub stamped "
            + "`sender: mesh/{id}`. A create issued from the DI root hub must leave from the "
            + "off-router execution hub (NodeOperationIssuingHub) instead");
        Reports().Where(r => r.Role.Contains("target", StringComparison.Ordinal)).Should().BeEmpty(
            "#1140's second production line is `CreateNodeResponse … target: mesh/{id}` — the reply "
            + "addressed straight back at the router, which follows from the request's sender and "
            + "makes the router run the continuation of someone else's write");
        Origins().Should().BeEmpty(
            "the ORIGIN detector must be silent too — it fires where the delivery is created, so it "
            + "sees a violating post even when the receiving hub's own report is muted");
    }

    /// <summary>
    /// 🚨 <b>The RUNTIME pin for <see href="https://github.com/Systemorph/MeshWeaver/issues/4463">#4463</see>
    /// — the operator recycle (the Recycle tool / Compile button).</b>
    ///
    /// <para><b>Why the source ratchet is not enough on its own.</b> Every other test of this verb
    /// drives it through a <c>SessionHubFactory</c> hub, whose non-mesh address makes
    /// <c>NodeOperationIssuingHub()</c> the IDENTITY FUNCTION — so none of them executes the branch
    /// #4463 is about, and reverting <c>RecycleCore</c> to <c>hub.Post</c> would leave them all
    /// green. <c>RouterAsNodeOperationOriginRatchetGuard</c> would catch the revert, but it reads
    /// SOURCE; this reads what the detector actually logged, which is the artefact production
    /// produced.</para>
    ///
    /// <para><b>The shape is production's.</b> #4463's line was posted from
    /// <c>MeshWeaver.AI.MeshOperations+&lt;&gt;c__DisplayClass95_0.&lt;RecycleCore&gt;b__3</c> with
    /// <c>sender: mesh/…</c> — an agent-surface <c>MeshOperations</c> built over the DI-injected
    /// hub, which in the mesh's root container IS the router. <c>new MeshOperations(Mesh)</c> is
    /// that, verbatim.</para>
    ///
    /// <para><b>The positive anchor is not decoration.</b> The verb REFUSES rather than posting when
    /// the release-request stamp is denied, and a refusal emits no <c>DisposeRequest</c> at all — so
    /// "no router traffic" would be trivially true of a recycle that never ran. Asserting
    /// <c>status=Recycled</c> first is what makes the silence below mean something.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOperatorRecycleIssuedFromTheRootMeshHub_NeverPostsTheTeardownAsTheRouter()
    {
        var path = await SeedNode("RouterTrafficOperatorRecycleProbe");

        var answer = await new MeshOperations(Mesh).Recycle(path)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        using var envelope = JsonDocument.Parse(answer);
        envelope.RootElement.GetProperty("status").GetString().Should().Be("Recycled",
            "the verb has to have RUN — it answers a refusal without posting any DisposeRequest at "
            + "all, and 'no router traffic' is trivially true of an operation that never happened");

        DumpReports();
        TeardownOrigins().Should().BeEmpty(
            "#4463: the teardown was POSTED with the mesh hub as sender, from this exact frame. "
            + "RecycleCore must issue it on NodeOperationIssuingHub(), which is the identity "
            + "function for every non-router caller and the off-router execution hub for this one");
        TeardownReports().Should().BeEmpty(
            "and the receiving hub must not see the router at an end of the teardown either");
    }

    /// <summary>
    /// 🚨 <b>The same runtime pin for the framework's ONE recycle surface,
    /// <c>HubRecycleExtensions.RecycleNode</c>.</b> Its own tests deliberately drive it through
    /// <c>RequestHub</c> — again a non-mesh address, again the seam as identity — so a regression to
    /// <c>hub.Post</c> would keep them green. This is the root-hub branch, with the router-traffic
    /// capture as the instrument.
    ///
    /// <para>The positive anchor here is the emission itself: <c>RecycleNode</c> answers only once
    /// the address has served a read again, so a node coming back proves the teardown was posted,
    /// executed, and the hub re-activated.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARecycleNodeIssuedFromTheRootMeshHub_NeverPostsTheTeardownAsTheRouter()
    {
        var path = await SeedNode("RouterTrafficRecycleNodeProbe");

        var node = await Mesh.RecycleNode(path)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        node.Should().NotBeNull(
            "RecycleNode answers only once the recycled address serves a read again, so this "
            + "emission is the proof that the teardown was actually posted and executed");

        DumpReports();
        TeardownOrigins().Should().BeEmpty(
            "a teardown issued from the root hub makes the ROUTER the sender of a work delivery — "
            + "the #4463 shape, one call frame further out");
        TeardownReports().Should().BeEmpty(
            "and the receiving hub must not see the router at an end of the teardown either");
    }

    /// <summary>
    /// 🚨 <b>The RUNTIME pin for #1140's remaining half — a NON-lifecycle delivery on a field the
    /// class itself has declared router-capable.</b>
    ///
    /// <para><b>Why this is a different test from the recycle ones above.</b> Those drive
    /// <c>DisposeRequest</c>, the one message on their receiver that the message-keyed source
    /// ratchet can see. <c>DataChangeRequest</c> is not a lifecycle message and never will be — no
    /// framework handler registers it as one — so nothing in
    /// <c>RouterAsNodeOperationOriginRatchetGuard</c>'s derived denominator covers it. It is
    /// nevertheless posted from the SAME <c>hub</c> field that <c>MeshNodeEditor.Move</c> hops ten
    /// lines below it, to a per-node address, and therefore produced exactly #1140's receiver-side
    /// line: <c>RawJson has the mesh hub as sender (sender: mesh/…, target: &lt;node path&gt;)</c>.
    /// One class, one field, two messages, and only one of them was hopped.</para>
    ///
    /// <para><b>And why the SOURCE ratchet is not enough on its own, again.</b> Every other test of
    /// this editor drives it through a session or client hub, where both seams are the IDENTITY
    /// FUNCTION — so reverting <c>MeshNodeEditor.Update</c> to <c>hub.Post</c> leaves them all
    /// green. <c>new MeshNodeEditor(Mesh, path)</c> is the root-hub branch, and this reads what the
    /// detector actually logged rather than what the source says.</para>
    ///
    /// <para><b>What it pins is the OUTCOME, not the mechanism — which is why it survived the
    /// mechanism changing under it.</b> The first fix hopped the bespoke post onto the issuing seam;
    /// the review round replaced it with the canonical
    /// <c>workspace.GetMeshNodeStream(path).Update(…)</c>, whose cache hub is off the router by
    /// construction. Measured across that change, this edit now emits NO router-traffic record at
    /// all rather than a correctly-addressed one — the exchange stopped existing rather than moving.
    /// Either way the assertion is the same and a revert to <c>hub.Post</c> reproduces #1140's line
    /// from this exact frame.</para>
    ///
    /// <para><b>The positive anchor is not decoration.</b> <c>Update</c> RETURNS WITHOUT WRITING
    /// when its <c>BehaviorSubject</c> has not yet seen the node (<c>if (current is null) return;</c>),
    /// so "no router traffic" would be trivially true of an editor that never got its first
    /// snapshot. Awaiting the node — and then the edited value coming back through the same live
    /// subscription — is what makes the silence below mean something. It is also the only thing that
    /// would catch the write being dropped outright: the stream's <c>Update</c> is a COLD
    /// observable, so an unsubscribed one silently does nothing.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANodeEditIssuedFromTheRootMeshHub_NeverPostsTheDataChangeAsTheRouter()
    {
        var path = await SeedNode("RouterTrafficEditorProbe");
        const string edited = "Edited by the router-traffic probe";

        using var editor = new MeshNodeEditor(Mesh, path);

        // The editor posts nothing until its own subscription has delivered a snapshot, so wait for
        // the condition rather than for a duration.
        await editor.Node.FirstAsync().Await(TestContext.Current.CancellationToken);

        editor.Update(n => n with { Name = edited });

        var applied = await editor.Node
            .Where(n => n.Name == edited)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        applied.Name.Should().Be(edited,
            "the edit has to have LANDED — Update returns without writing anything at all when the "
            + "editor has no snapshot yet, and its stream write is a COLD observable, so 'no router "
            + "traffic' is trivially true of a write that never happened");

        DumpReports();
        // 🚨 The ORIGIN side is asserted WHOLESALE, and that is a measurement rather than optimism:
        // this seeded edit emits zero origin records today, and the origin line names its own call
        // site — so any record here is both a real violation and immediately actionable, which is
        // exactly the assertion worth being strict about.
        Origins().Should().BeEmpty(
            "#1140: an edit driven from the ROOT mesh hub must put the router on no end of any "
            + "delivery it causes. Before the fix this frame produced `DataChangeRequest … sender: "
            + "mesh/{id}` straight from MeshNodeEditor.Update; the write now goes through the node's "
            + "own stream, whose cache hub is off the router by construction");
        // The RECEIVER side stays filtered on the write exchange. It reports a routed payload as
        // RawJson, so it cannot attribute a line to a caller — a blanket assertion there would red
        // on any unrelated pre-existing line and teach the next reader to widen it rather than read
        // it.
        Reports().Where(r => r.MessageType is nameof(DataChangeRequest) or nameof(DataChangeResponse)
                        or nameof(PatchDataChangeRequest))
            .Should().BeEmpty(
                "and neither end of the write exchange may be the router at the receiving hub "
                + "either — the reply addressed back at mesh/{id} is #1140's second production line");
    }

    /// <summary>
    /// 🚨 <b>The RUNTIME pin for the SUBSCRIPTION family —
    /// <see href="https://github.com/Systemorph/MeshWeaver/issues/4614">#4614</see>,
    /// <see href="https://github.com/Systemorph/MeshWeaver/issues/4615">#4615</see> and
    /// <see href="https://github.com/Systemorph/MeshWeaver/issues/4617">#4617</see>, which are ONE
    /// defect seen from both ends.</b>
    ///
    /// <para>Production filed three tickets, minutes apart, off one <c>PearlTechnology/CompanyProfile</c>
    /// render on <c>memex</c> at 2026-09-17 12:58:47Z:</para>
    ///
    /// <code>
    /// ORIGIN: SubscribeRequest  … as sender (sender: mesh/q8f5…, target: PearlTechnology/CompanyProfile)
    /// ORIGIN: SubscribeAck      … as target (sender: PearlTechnology/CompanyProfile, target: mesh/q8f5…)
    /// ORIGIN: StreamEndedEvent  … as target (sender: PearlTechnology/CompanyProfile, target: mesh/q8f5…)
    /// </code>
    ///
    /// <para>The second and third are not separate defects and cannot be fixed where they are
    /// posted: the owner answers <c>ResponseFor(delivery)</c> and fans out to
    /// <c>request.Subscriber</c>, both of which ARE the subscribe's sender. So the only address in
    /// the family a caller chooses is the one the <c>SubscribeRequest</c> leaves from — and the
    /// remedy the origin line SUGGESTS (<c>ReadIssuingHub()</c>) is not available for it, because
    /// <c>portal/reads-{meshId}</c> registers no handlers: it would receive the fan-out and route it
    /// nowhere. <c>StreamSubscribingHub()</c> is the data-wired seam that can actually be a
    /// subscriber.</para>
    ///
    /// <para><b>Why the source ratchets cannot see this.</b> <c>SubscribeRequest</c> is not a
    /// lifecycle message, so the message-keyed guard's derived denominator excludes it; and the
    /// receiver-keyed guard reads POSTS, while this delivery is created inside
    /// <c>JsonSynchronizationStream.CreateExternalClient</c> on whatever hub the caller's WORKSPACE
    /// belongs to — a workspace is scoped per hub, so the choice is made by
    /// <c>hub.GetWorkspace()</c>, which is not a post at all.</para>
    ///
    /// <para><b>The positive anchor is not decoration.</b> <c>RenderArea</c> answers
    /// <c>"Not found: …"</c> / <c>"Error: …"</c> as ordinary emissions WITHOUT opening any stream,
    /// and its last gate faults before subscribing when the budget is spent — so "no router
    /// traffic" is trivially true of a render that never subscribed. Requiring a real
    /// <c>{areas, data}</c> frame is what makes the silence below mean something.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARenderAreaIssuedFromTheRootMeshHub_NeverSubscribesAsTheRouter()
    {
        var path = await SeedNode("RouterTrafficRenderAreaProbe");

        var frame = await new MeshOperations(Mesh)
            .RenderArea(path, MeshNodeLayoutAreas.OverviewArea)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        frame.Should().StartWith("{",
            "the render has to have RUN — a 'Not found: …' / 'Error: …' answer is returned without "
            + "opening any stream at all, and 'no router traffic' is trivially true of a "
            + "subscription that never happened");
        using var envelope = JsonDocument.Parse(frame);
        envelope.RootElement.TryGetProperty("areas", out var areas).Should().BeTrue(
            "the frame the verb promises is the owner's materialised {areas, data} snapshot — "
            + "anything else means the subscribe did not complete and the assertions below would "
            + "be measuring an operation that never reached the owner");
        areas.ValueKind.Should().Be(JsonValueKind.Object);

        DumpReports();
        SubscriptionOrigins().Should().BeEmpty(
            "#4614/#4615/#4617: a layout-area render driven from the ROOT mesh hub must not make "
            + "the router the SUBSCRIBER of a synchronization stream. The subscribe's sender is "
            + "also the address the owner acks, fans out to and announces the end of — and the hub "
            + "that must host the sync/{streamId} sub-hub those frames are routed to — so it has to "
            + "be StreamSubscribingHub(), the identity function for every non-router caller");
        SubscriptionReports().Should().BeEmpty(
            "and the receiving hubs must not see the router at an end of the subscription either");
    }

    /// <summary>
    /// The subscription family, at the ORIGIN site — which always carries the real CLR type, so
    /// this filter is exact there. Narrow on purpose: this test pins ONE defect, and a blanket
    /// "no router traffic anywhere" assertion would red on any unrelated pre-existing line and
    /// teach the next reader to widen it rather than read it.
    /// </summary>
    private RouterTrafficRecord[] SubscriptionOrigins() =>
        Origins().Where(r => IsSubscriptionFamily(r.MessageType)).ToArray();

    /// <summary>
    /// The same family at the RECEIVER site, plus <c>RawJson</c> — which is what a routed delivery
    /// is reported as there, and is exactly the shape #1140's production lines carry. Including it
    /// is safe here rather than blanket: xUnit builds a fresh instance (and a fresh capture) per
    /// test method, so the only traffic in this record set is the seed create and this one render.
    /// </summary>
    private RouterTrafficRecord[] SubscriptionReports() =>
        Reports().Where(r => IsSubscriptionFamily(r.MessageType) || r.MessageType == "RawJson")
            .ToArray();

    /// <summary>
    /// 🚨 EVERY message the owner addresses at <c>request.Subscriber</c>, in one place so the two
    /// filters above cannot drift apart. <c>StreamErrorEvent</c> is in the list although this
    /// render's happy path never emits one: the owner posts it to the same address as the rest
    /// (<c>JsonSynchronizationStream.cs:1604</c>), so a regression that left only the error leg
    /// pointed at the router would otherwise pass unseen (Copilot on #4622).
    /// </summary>
    private static bool IsSubscriptionFamily(string messageType) =>
        messageType is nameof(SubscribeRequest) or nameof(SubscribeAck)
            or nameof(DataChangedEvent) or nameof(StreamEndedEvent)
            or nameof(StreamErrorEvent) or nameof(UnsubscribeRequest);

    /// <summary>The node the recycle targets has to exist before it can be torn down.</summary>
    private async Task<string> SeedNode(string id)
    {
        var created = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);
        created.Path.Should().Be($"{TestPartition}/{id}",
            "a recycle of a node that was never created would tear nothing down and emit no "
            + "traffic, making the assertions that follow vacuous");
        return created.Path;
    }

    /// <summary>
    /// 🚨 Filtered on the TEARDOWN specifically, not on "any record". The origin site always carries
    /// the real CLR type, so this is exact there; the receiver side reports a routed payload as
    /// <c>RawJson</c>, so the companion filter below also counts a record whose ends match the
    /// teardown's. Keeping the filter narrow is deliberate: these two tests pin ONE defect, and a
    /// blanket "no router traffic anywhere" assertion would red on any unrelated pre-existing line
    /// and teach the next reader to widen it rather than read it.
    /// </summary>
    private RouterTrafficRecord[] TeardownOrigins() =>
        Origins().Where(r => r.MessageType == nameof(DisposeRequest)).ToArray();

    private RouterTrafficRecord[] TeardownReports() =>
        Reports().Where(r => r.MessageType == nameof(DisposeRequest)).ToArray();

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL, and it is not optional. The measurement above reads "no records" as
    /// "the seam held" — a reading that is valid only if this fixture's capture provider is really
    /// in the mesh's logging pipeline and the detector is really armed on these hubs. So make the
    /// router an END on purpose and require the line.
    ///
    /// <para>A plain <c>Post</c> to a client hub, deliberately: it is the "sender" role — the same
    /// role #1140's first line reports — and it needs no node CRUD, so a failure here cannot be
    /// confused with a failure of the create path. The arrival subject is the barrier rather than a
    /// sleep: <c>ReportRouterTraffic</c> runs synchronously at the top of <c>DeliverMessage</c>,
    /// strictly before the delivery reaches the handler that completes it; the ORIGIN report is
    /// made synchronously inside <c>Post</c>, so it is already recorded when that method returns.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork()
    {
        var client = GetClient();

        // 🚨 Posted FROM the router on purpose — producing the violating shape IS this test's
        // subject. `Post`, not `Observe`: no response is wanted, and the router-as-request-origin
        // ratchet (#2423) is about request/response exchanges a test could have issued elsewhere.
        Mesh.Post(new RouterOriginProbe(), o => o.WithTarget(client.Address));

        await _probeArrived.Should().Within(TestTimeouts.Convergence)
            .Emit("the probe must actually reach the client hub, or nothing was delivered and this "
                + "control proves nothing", cancellationToken: TestContext.Current.CancellationToken);

        DumpReports();
        // 🚨 Asserted on ROLE and ENDS, never on the message type. The client hub is reached over a
        // serializing route, so the delivery arrives packed — the detector reads `RawJson`, which is
        // precisely the type #1140's production lines carry. Keying this control on
        // `nameof(RouterOriginProbe)` found 0 records and would have read as "the capture is dead".
        var report = Reports().Should().ContainSingle(
            "the capture must be able to record a genuine violation, or the measurement's green is "
            + "an instrument that cannot fail rather than a seam that holds").Subject;
        report.Role.Should().Be("sender");
        report.Sender.Should().Be(Mesh.Address.ToString(),
            "the recorded line must name the delivery's own sender — the router — not whichever hub "
            + "happened to log it");
        report.Target.Should().Be(client.Address.ToString());

        // 🚨 The ORIGIN half — the one #1140 needed and did not have. It must name the frame that
        // posted, which here is this test method.
        var origin = Origins().Should().ContainSingle(
            "the origin detector must report the same violation from the POSTING side").Subject;
        origin.Role.Should().Be("sender");
        origin.Sender.Should().Be(Mesh.Address.ToString());
        origin.CallSite.Should().Contain(nameof(TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork),
            "a call site that does not name the caller is the property #1140 was missing — the "
            + "receiver-side line already carries the two addresses, and four re-filings over a "
            + "month could not turn them into a call site");
    }

    /// <summary>
    /// 🚨 <b>The premise the whole rule rests on: adopting a seam is NEVER a behaviour change.</b>
    ///
    /// <para>"Hop every targeted post off a router-capable receiver" is a RULE rather than a
    /// judgement call for exactly one reason — both seams are the IDENTITY FUNCTION for any hub
    /// whose address type is not the mesh type, so a site already off the router is byte-for-byte
    /// unaffected and only a site that is not is corrected. That premise is stated in
    /// <c>RouterAsRouterCapableReceiverRatchetGuard</c>, in both allow files, in
    /// Doc/Architecture/RouterTrafficDetection and at every call site that adopts a seam — and
    /// until this test nothing measured it.</para>
    ///
    /// <para>It is what makes the swaps no runtime test reaches safe to make at all: several of the
    /// exchanges this class covers (the script dispatch, the content-collection reads) have no
    /// end-to-end suite, and every existing test of them runs through a session or client hub. If
    /// the seams are the identity there, those swaps changed nothing for them; if they are not,
    /// every one of those sites silently moved hub and the claim in the comments is false. Asserted
    /// on REFERENCE identity, not on address equality: a second hub at the same address would still
    /// be a different actor with a different action block.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void EverySeam_IsTheIdentityFunction_ForAHubThatIsNotTheRouter()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var client = GetClient();
        client.Address.Type.Should().NotBe(Mesh.Address.Type,
            "the premise is about a NON-router hub, so this fixture has to hand us one — if the "
            + "client's address type ever became the mesh type, the assertions below would be "
            + "measuring the router and passing for the wrong reason");

        client.NodeOperationIssuingHub().Should().BeSameAs(client,
            "NodeOperationIssuingHub must return the hub UNCHANGED off the router — otherwise every "
            + "site that adopted it moved its deliveries to a different action block, and 'this is "
            + "a no-op wherever the router is not reached' is false in every comment that says it");
        client.ReadIssuingHub().Should().BeSameAs(client,
            "and the same for ReadIssuingHub, which is the seam most of #1140's remaining sites "
            + "adopted");
        client.StreamSubscribingHub().Should().BeSameAs(client,
            "and the same for StreamSubscribingHub (#4614) — this one carries the MOST weight, "
            + "because the subscriber address is not just where a reply lands: it is the key the "
            + "owner's per-subscriber bookkeeping and the workspace's remote-stream cache use, so a "
            + "seam that silently moved a non-router subscriber would re-key every live stream");

        // …and the other half: ON the router both seams must hand back something ELSE, or the hop
        // is a no-op there too and nothing was fixed.
        Mesh.NodeOperationIssuingHub().Should().NotBeSameAs(Mesh,
            "on the ROUTER the seam has to actually move the delivery's origin — a seam that is the "
            + "identity function everywhere is decoration");
        Mesh.ReadIssuingHub().Should().NotBeSameAs(Mesh,
            "the read seam likewise");
        Mesh.StreamSubscribingHub().Should().NotBeSameAs(Mesh,
            "and the subscription seam, which is what takes the router off #4614/#4615/#4617's "
            + "whole delivery family");
        Mesh.StreamSubscribingHub().Should().NotBeSameAs(Mesh.ReadIssuingHub(),
            "and it must be a DIFFERENT hub from the read seam — portal/reads-{meshId} registers no "
            + "handlers by design, so it has no RouteStreamMessage route and could never deliver the "
            + "owner's fan-out to the sync/{streamId} sub-hub");
    }

    /// <summary>The receiver-side lines — <c>ROUTER_TRAFFIC:</c>, logged in <c>DeliverMessage</c>.</summary>
    private RouterTrafficRecord[] Reports() => _capture.Records.Where(r => !r.IsOrigin).ToArray();

    /// <summary>The origin-side lines — <c>ROUTER_TRAFFIC ORIGIN:</c>, logged in <c>Post</c>.</summary>
    private RouterTrafficRecord[] Origins() => _capture.Records.Where(r => r.IsOrigin).ToArray();

    private void DumpReports()
    {
        foreach (var record in _capture.Records)
            Output.WriteLine($"ROUTER_TRAFFIC captured: {record}");
    }

    private sealed record RouterTrafficRecord(
        bool IsOrigin, string MessageType, string Role, string Sender, string Target, string CallSite);

    /// <summary>
    /// Reads the detector's own ERROR out of the logging pipeline — structured state, not the
    /// formatted string, so the assertions pin the VALUES the detector chose rather than the prose
    /// around them. Same shape as <c>RouterTrafficDetectorTest</c>'s capture; duplicated rather than
    /// shared because that one lives in a different test assembly on a different base.
    /// </summary>
    private sealed class RouterTrafficCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RouterTrafficRecord> _records = new();

        internal RouterTrafficRecord[] Records => _records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_records);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<RouterTrafficRecord> sink) : ILogger
        {
            private sealed class NullScope : IDisposable
            {
                internal static readonly NullScope Instance = new();
                public void Dispose() { }
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                var text = formatter(state, exception);
                var isOrigin = text.StartsWith("ROUTER_TRAFFIC ORIGIN:", StringComparison.Ordinal);
                if (!isOrigin && !text.StartsWith("ROUTER_TRAFFIC:", StringComparison.Ordinal))
                    return;

                sink.Enqueue(new RouterTrafficRecord(
                    isOrigin,
                    Value(values, "MessageType"),
                    Value(values, "Role"),
                    Value(values, "Sender"),
                    Value(values, "Target"),
                    Value(values, "CallSite")));
            }

            private static string Value(IReadOnlyList<KeyValuePair<string, object?>> values, string key)
            {
                foreach (var pair in values)
                    if (pair.Key == key)
                        return pair.Value?.ToString() ?? "(null)";
                return "(absent)";
            }
        }
    }
}
