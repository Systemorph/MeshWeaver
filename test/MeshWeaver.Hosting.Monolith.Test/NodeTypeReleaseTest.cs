using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Focused tests for the Release-MeshNode-on-compile flow introduced in
/// <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>. Verifies:
///
/// <list type="number">
///   <item>Flipping <c>RequestedReleaseAt</c> + <c>ReleaseNotes</c> on the
///     NodeType via <c>workspace.GetMeshNodeStream(path).Update(...)</c> results
///     in a <c>Release</c> MeshNode at <c>{nodeTypePath}/Release/{version}</c>.</item>
///   <item>The Release carries the markdown notes the user wrote on the
///     NodeType.</item>
/// </list>
///
/// Drives the request via the canonical "node mutations go through
/// <c>stream.Update(...)</c>" pattern (see CLAUDE.md +
/// <c>Doc/Architecture/RequestViaStreamUpdate.md</c>). The bespoke
/// <c>CreateReleaseRequest</c> handler used to race the per-NodeType hub's
/// compile watcher and wedge the hub on CI; the stream-update path is
/// race-free by construction — the watcher reacts to a property patch on the
/// node's own state.
/// </summary>
public class NodeTypeReleaseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ReleaseTestPartition = "TestRelease";
    private const string NodeTypeId = "Sample";
    private const string NodeTypePath = $"{ReleaseTestPartition}/{NodeTypeId}";

    [Fact(Timeout = 120000)]
    public async Task CompilationPending_CreatesReleaseMeshNode_WithNotes()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = Mesh.GetWorkspace();

        // 1. Create the NodeType. The per-NodeType hub's auto-watcher
        //    (`InstallCompileWatcher`) flips Pending on activation
        //    (HasUsableBuild=false) and produces a kickoff Release with no notes.
        await NodeFactory.CreateNode(new MeshNode(NodeTypeId, ReleaseTestPartition)
        {
            Name = "Sample Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Sample for the Release-on-compile test.",
                Configuration = "config => config.AddDefaultLayoutAreas()"
            }
        });

        // 2. Wait for the kickoff compile to settle. Reading via the live
        //    MeshNode stream is path-known and authoritative; ObserveQuery is
        //    eventually consistent and would miss the post-compile tick.
        var kickoffSnapshot = await workspace
            .GetMeshNodeStream(NodeTypePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath))
            .Take(1)
            .Timeout(45.Seconds())
            .ToTask(ct);
        var kickoffReleasePath = ((NodeTypeDefinition)kickoffSnapshot.Content!).LatestReleasePath!;

        // 3. Author ReleaseNotes via `stream.Update` (the same path the UI's
        //    Configuration form's TextAreaControl auto-saves through) and
        //    wait for the write to be observable on the mesh-hub-cached view.
        //    The wait is the canonical sync point for any downstream read of
        //    the NodeType — every consumer (`EnrichWithNodeType`,
        //    `NodeTypeCompileActivityHandler.Handle`'s `pendingNode` capture,
        //    etc.) reads through that cache; gating the trigger on it being
        //    caught up guarantees the explicit-release compile observes the
        //    just-written notes.
        var releaseNotes = "First release of Sample. **Bold** + _italic_ + a list:\n- one\n- two";
        await workspace.GetMeshNodeStream(NodeTypePath).Update(curr =>
            {
                if (curr?.Content is not NodeTypeDefinition def) return curr!;
                return curr with
                {
                    Content = def with { ReleaseNotes = releaseNotes }
                };
            })
            .FirstAsync()
            .ToTask(ct);

        await workspace.GetMeshNodeStream(NodeTypePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && string.Equals(d.ReleaseNotes, releaseNotes, StringComparison.Ordinal))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);

        // 4. Fire the explicit-release trigger via the canonical
        //    `RequestedReleaseAt + RequestedReleaseForce` flip. The
        //    per-NodeType hub's `InstallReleaseRequestWatcher` sees the
        //    timestamp move past `LastReleaseRequestHandledAt` and flips
        //    `CompilationStatus = Pending` on the same node — the
        //    `InstallCompileWatcher` then runs Roslyn and the resulting
        //    activity captures the (now visible) `ReleaseNotes`.
        //
        //    No bespoke `CreateReleaseRequest` / `UpdateNodeRequest` — both
        //    were racing the compile watcher (a separate inflight activity per
        //    request leaked DataChangeRequest callbacks on the mesh hub and
        //    wedged the per-NodeType hub on CI).
        var triggerAt = DateTimeOffset.UtcNow;
        await workspace.GetMeshNodeStream(NodeTypePath).Update(curr =>
            {
                if (curr?.Content is not NodeTypeDefinition def) return curr!;
                return curr with
                {
                    Content = def with
                    {
                        RequestedReleaseAt = triggerAt,
                        RequestedReleaseForce = true
                    }
                };
            })
            .FirstAsync()
            .ToTask(ct);

        // 4. Wait for the recompile triggered by this trigger to land —
        //    `LastReleaseRequestHandledAt >= triggerAt` is the watermark the
        //    watcher stamps when it picks up the trigger; combined with
        //    `CompilationStatus == Ok` and a fresh `LatestReleasePath` (≠
        //    kickoff), we know the explicit-trigger release is the one
        //    currently active on the NodeType.
        var settled = await workspace.GetMeshNodeStream(NodeTypePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.LastReleaseRequestHandledAt is { } h && h >= triggerAt
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath)
                && d.LatestReleasePath != kickoffReleasePath)
            .Take(1)
            .Timeout(60.Seconds())
            .ToTask(ct);
        var newReleasePath = ((NodeTypeDefinition)settled.Content!).LatestReleasePath!;

        // 5. Read the new Release MeshNode directly by path — no lagged
        //    namespace query, no race with index propagation.
        var release = await workspace.GetMeshNodeStream(newReleasePath)
            .Where(n => n is not null && n.Content is NodeTypeRelease)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);

        release.Should().NotBeNull();
        release.NodeType.Should().Be(ReleaseNodeType.NodeType);
        release.MainNode.Should().Be(NodeTypePath);
        release.Path.Should().StartWith(NodeTypePath + "/Release/");

        var releaseContent = release.Content as NodeTypeRelease;
        releaseContent.Should().NotBeNull("the Release MeshNode must carry a NodeTypeRelease payload");
        releaseContent!.NodeTypePath.Should().Be(NodeTypePath);
        releaseContent.Status.Should().Be("Succeeded");
        releaseContent.AssemblyPath.Should().NotBeNullOrEmpty();
        releaseContent.Notes.Should().NotBeNull();
        releaseContent.Notes!.Content.Should().Contain("First release of Sample");
        releaseContent.Notes.Content.Should().Contain("Bold");
    }
}
