using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Deterministic repro for Systemorph/MeshWeaver#2191 — <b>an OPEN live-bound layout area stays on
/// the pre-write snapshot after a cross-hub node update</b>. The user watches a node's default
/// area, an agent patches the node from a side-panel thread, the write demonstrably lands
/// (<c>GetVersions</c> shows v+1) — and the open view keeps rendering the old content until the
/// page is reloaded or the node is Recycled.
///
/// <para>The precondition that makes it happen is the one the issue names: the <b>owner-side</b>
/// half of the area's synchronization stream has ended (an idle release, an owner-side
/// unsubscribe, a per-node hub deactivation) while the CLIENT half is still perfectly healthy —
/// its hub is <c>Started</c>, its stream undisposed, its replay subject still handing out the last
/// snapshot it ever saw. Nothing tells the attached subscriber, and the workspace's cache liveness
/// check cannot see it either.</para>
///
/// <para>The tests drive that state with an owner-side unsubscribe on a HEALTHY owner, and assert
/// the only things that matter: the subscription comes back <b>from the end alone, with nobody
/// writing the node</b>, and the area the browser is bound to shows new content without a Recycle
/// or a re-bind.</para>
///
/// <para>🚨 There is deliberately NO test for the owning hub itself tearing down, because the fix
/// deliberately stays silent there. Announcing out of a dying owner means routing a message to a
/// deactivating address, which on Orleans RE-ACTIVATES it — that version of the fix kept a
/// deactivated grain in the silo catalog and broke
/// <c>OrleansGrainTeardownStragglerTest</c> / <c>OrleansMeshTests.HubWorksAfterDisposal</c>. An
/// owner going away is already covered by the recycle re-arm and the change-feed latch; see the
/// SCOPE note in <c>JsonSynchronizationStream.CreateSynchronizationStream</c>.</para>
/// </summary>
[Collection("StaleLiveBoundAreaTests")]
public class StaleLiveBoundAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddMeshWeaverDocs()
            .AddDoc()
            .AddDocumentation()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .AddData(data => data)
            .WithType<MeshNode>("MeshNode")
            .WithType<MarkdownContent>("MarkdownContent");

    /// <summary>
    /// 🚨 THE DEFECT, pinned at the seam it lives at. When the owner's half of a subscription ENDS,
    /// the subscriber must LEARN it — from the end itself, not from an unrelated future write.
    ///
    /// <para>Pre-fix the owner said NOTHING when its half ended: the mirror kept replaying its last
    /// snapshot, and its ONLY route back was the change-feed latch — which fires when somebody
    /// happens to WRITE the owner's node and that event reaches this subscriber. This test writes
    /// NOTHING. Pre-fix nothing ever happens and it times out; post-fix the announcement
    /// re-establishes the subscription on its own.</para>
    ///
    /// <para>The observable claim is the owner serving this stream again — a FRESH
    /// <c>sync/{streamId}</c> hub instance under the owning node, distinct from the one that was
    /// ended — which is "the mirror is live again" stated where it cannot be confused with a cached
    /// replay.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task OwnerSideSyncEnd_ReEstablishesTheSubscription_WithNobodyWriting()
    {
        var (_, nodeAddress, _, stream) = await OpenLiveBoundOverview("Nothing will be written");

        var ended = await EndOwnerSideSync(nodeAddress, stream);

        // No write. No Recycle. No re-bind. The END is the only event in play.
        var reEstablished = await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .Select(_ => OwnerSideSyncHub(nodeAddress, stream.StreamId))
            .Where(h => h is { RunLevel: <= MessageHubRunLevel.Started } && !ReferenceEquals(h, ended))
            .FirstAsync()
            .Timeout(60.Seconds())
            .Await();
        reEstablished.Should().NotBeNull(
            "the owner must re-serve this subscription after ending it — a mirror has to learn from "
            + "the END itself that it is orphaned, never from an unrelated later write to the node (#2191)");
        Output.WriteLine("✅ The subscription was re-established with nobody writing the node.");
    }

    /// <summary>
    /// The user-visible claim: with the owner-side sync ended underneath it, an already-open area
    /// still picks up a cross-hub write to the node it is bound to — no Recycle, no page reload.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task OpenArea_SeesCrossHubUpdate_AfterOwnerSideSyncEnded()
    {
        var (nodePath, nodeAddress, reference, stream) = await OpenLiveBoundOverview("Before the write");

        // ── The precondition: end the OWNER's half of this subscription, leaving the client half
        //    untouched. This is mechanically what an owner-side idle release / per-node hub
        //    deactivation does — the owner's per-subscriber `sync/` hub disposes and the client is
        //    never told.
        await EndOwnerSideSync(nodeAddress, stream);

        // ── The write: cross-hub, through the ONE mutation API, from a hub that does not own the
        //    node (exactly what an agent Patch / MCP patch does).
        const string AfterText = "After the cross-hub write";
        await WriteMarkdownCrossHub(nodePath, AfterText);

        // ── The claim: the OPEN area — the thing the browser renders — shows the new content.
        await WaitForAreaToRender(stream, AfterText,
            "an open live-bound area must observe a cross-hub node update without a Recycle or a page reload (#2191)");
        Output.WriteLine("✅ The open area rendered the new content.");
    }

    /// <summary>
    /// The second half of the issue's repro: a FRESH bind taken after the write must render the
    /// new content, never the cached pre-write snapshot the workspace still holds.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task FreshBind_AfterCrossHubUpdate_DoesNotServeTheCachedSnapshot()
    {
        var (nodePath, nodeAddress, reference, stream) = await OpenLiveBoundOverview("Fresh-bind before");

        await EndOwnerSideSync(nodeAddress, stream);

        const string AfterText = "Fresh-bind after";
        await WriteMarkdownCrossHub(nodePath, AfterText);

        // A second reader binds the same (owner, reference) pair — the cache must not hand it the
        // dead stream's stale replay.
        var workspace = GetClient().GetWorkspace();
        var rebound = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);
        await WaitForAreaToRender(rebound, AfterText,
            "a fresh bind after the write must render the current node, not the dead stream's cached snapshot (#2191)");
        Output.WriteLine("✅ The fresh bind rendered the new content.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Markdown node and binds its default (Overview) area over a REMOTE layout-area
    /// stream — the exact shape <c>LayoutAreaView.BindStream</c> uses for a non-local address —
    /// then waits for the initial content to render.
    /// </summary>
    private async Task<(string nodePath, Address nodeAddress, LayoutAreaReference reference, ISynchronizationStream<JsonElement> stream)>
        OpenLiveBoundOverview(string initialMarkdown)
    {
        var id = $"stale-{Guid.NewGuid().AsString()}";
        var node = new MeshNode(id, "StaleAreaSpace")
        {
            Name = "Stale area subject",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = initialMarkdown }
        };

        var created = await NodeFactory.CreateNode(node).Should().Emit();
        var nodePath = created.Path!;
        var nodeAddress = new Address(nodePath);
        Output.WriteLine($"Created markdown node at {nodePath}");

        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        await WaitForAreaToRender(stream, initialMarkdown,
            "the area must render the node's initial content before the write");
        Output.WriteLine("Area is open and live-bound.");
        return (nodePath, nodeAddress, reference, stream);
    }

    /// <summary>
    /// Writes new markdown to the node from a hub that does NOT own it, through the one sanctioned
    /// mutation API, and waits for the owner to have committed it (read-your-writes on the node
    /// stream — the same barrier the agent's Patch reports success on).
    /// </summary>
    private async Task WriteMarkdownCrossHub(string nodePath, string markdown)
    {
        var writerWorkspace = GetClient().GetWorkspace();
        writerWorkspace.GetMeshNodeStream(nodePath)
            .Update(current => MarkdownOverviewLayoutArea.WithMarkdownContent(
                current, markdown, writerWorkspace.Hub.JsonSerializerOptions))
            .Subscribe(_ => { }, ex => Output.WriteLine($"cross-hub update failed: {ex}"));

        (await Mesh.GetWorkspace().GetMeshNodeStream(nodePath)
                .Where(n => MarkdownOverviewLayoutArea.GetMarkdownContent(n).Contains(markdown))
                .Should().Within(60.Seconds()).Emit())
            .Should().NotBeNull("the cross-hub write itself must land on the owner (the issue's precondition)");
        Output.WriteLine($"Cross-hub write committed: '{markdown}'.");
    }

    /// <summary>
    /// Ends the OWNER's half of <paramref name="stream"/>'s subscription, leaving the client half
    /// untouched — mechanically what an owner-side idle release / per-node-hub deactivation does.
    ///
    /// <para>🚨 The precondition is ASSERTED BOTH WAYS so this step can never become a verification
    /// that cannot fail: the owner's <c>sync/{streamId}</c> hub must be PRESENT before the
    /// unsubscribe (otherwise the probe is looking in the wrong place and the whole test is
    /// vacuous), and that INSTANCE must be gone afterwards.</para>
    ///
    /// <para>🚨 "Gone" is by instance identity, not by absence. Post-fix the owner announces the end
    /// and the mirror re-asks at once, so a fresh hub can occupy the same address before a
    /// poll ever observes an empty slot — a wait for <c>null</c> then hangs for its whole budget
    /// while the system is behaving perfectly (measured: it did). Comparing against the instance we
    /// started with is the question actually being asked.</para>
    /// </summary>
    /// <returns>The owner-side hub instance that was ended, so callers can require a DIFFERENT one.</returns>
    private async Task<IMessageHub> EndOwnerSideSync(Address nodeAddress, ISynchronizationStream<JsonElement> stream)
    {
        var before = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => OwnerSideSyncHub(nodeAddress, stream.StreamId))
            .Where(h => h is not null)
            .FirstAsync()
            .Timeout(30.Seconds())
            .Await();
        before.Should().NotBeNull(
            "the owner must be serving a sync stream for this subscription — without that the "
            + "'owner-side sync ended' precondition is never established and the test proves nothing");
        Output.WriteLine($"Owner-side sync hub present ({before!.Address}); ending it…");

        stream.Hub.Post(new UnsubscribeRequest(stream.StreamId), o => o.WithTarget(nodeAddress));

        var after = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => OwnerSideSyncHub(nodeAddress, stream.StreamId))
            .Where(h => !ReferenceEquals(h, before))
            .FirstAsync()
            .Timeout(30.Seconds())
            .Await();
        Output.WriteLine(
            $"Owner-side sync ended (now: {(after is null ? "gone" : "a fresh instance")}).");
        return before;
    }

    /// <summary>The owner's per-subscriber <c>sync/{streamId}</c> hub, or null when it is gone.</summary>
    private IMessageHub? OwnerSideSyncHub(Address nodeAddress, string streamId)
    {
        var owner = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never);
        return owner?.GetHostedHub(SynchronizationAddress.Create(streamId), HostedHubCreation.Never);
    }

    /// <summary>
    /// Reactively waits until the area stream the VIEW is bound to carries
    /// <paramref name="expected"/>. This is the store <c>LayoutAreaView</c> renders from, so a
    /// change that lands here is a change the user sees — and one that does not is exactly the
    /// stale view the issue reports. No <c>Take(1)</c> on the binding, no <c>Task.Delay</c>.
    /// </summary>
    private static async Task WaitForAreaToRender(
        ISynchronizationStream<JsonElement> stream, string expected, string because)
        => (await stream
                .Where(item => item.Value.ValueKind != JsonValueKind.Undefined
                               && item.Value.GetRawText().Contains(expected))
                .Should().Within(60.Seconds()).Emit())
            .Should().NotBeNull(because);
}
