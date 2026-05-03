using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Focused tests for the Release-MeshNode-on-compile flow introduced in
/// <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>. Verifies:
///
/// <list type="number">
///   <item>Clicking Create Release (flipping <c>CompilationStatus = Pending</c>
///     with <c>ReleaseNotes</c> set) results in a <c>Release</c> MeshNode at
///     <c>{nodeTypePath}/_Release/{version}</c>.</item>
///   <item>The Release carries the markdown notes the user wrote on the
///     NodeType.</item>
///   <item>The NodeType's <c>LatestReleasePath</c> points at the new release.</item>
/// </list>
///
/// Doesn't exercise the full code-edit / recompile / re-read cycle —
/// CodeEditRecompileTest covers that. This test isolates the Release creation
/// invariant so a regression there shows up in a small, fast test.
/// </summary>
public class NodeTypeReleaseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ReleaseTestPartition = "TestRelease";
    private const string NodeTypeId = "Sample";
    private const string NodeTypePath = $"{ReleaseTestPartition}/{NodeTypeId}";

    [Fact(Timeout = 60000)]
    public async Task CompilationPending_CreatesReleaseMeshNode_WithNotes()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // 1. Create the NodeType with a trivial source. No Code source needed —
        // a NodeTypeDefinition with only a hub-config lambda compiles to a
        // valid (empty) assembly.
        await meshService.CreateNode(new MeshNode(NodeTypeId, ReleaseTestPartition)
        {
            Name = "Sample Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Sample for the Release-on-compile test.",
                Configuration = "config => config.AddDefaultLayoutAreas()"
            }
        });

        // 2. Click Create Release: flip CompilationStatus = Pending with
        //    ReleaseNotes set. Same shape as the GUI button.
        var releaseNotes = "First release of Sample. **Bold** + _italic_ + a list:\n- one\n- two";
        var nodeTypeNode = await meshService
            .QueryAsync<MeshNode>(MeshQueryRequest.FromQuery($"path:{NodeTypePath}"))
            .FirstOrDefaultAsync(ct);
        nodeTypeNode.Should().NotBeNull();

        await meshService.UpdateNode(nodeTypeNode! with
        {
            Content = (nodeTypeNode!.Content as NodeTypeDefinition)! with
            {
                ReleaseNotes = releaseNotes,
                CompilationStatus = CompilationStatus.Pending,
                LastCompileStartedAt = DateTimeOffset.UtcNow
            }
        });

        // 3. Subscribe to the Release subtree via the catalog change-feed and
        //    wait for the first Release node to appear. ObserveQuery re-emits
        //    on every catalog update — no polling, no per-hub SubscribeRequest.
        var releaseNamespace = $"{NodeTypePath}/_Release";
        var release = await meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{releaseNamespace} nodeType:Release"))
            .Select(change => change.Items.FirstOrDefault())
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask(ct);

        release.Should().NotBeNull();
        release!.NodeType.Should().Be(ReleaseNodeType.NodeType);
        release.MainNode.Should().Be(NodeTypePath);
        release.Path.Should().StartWith(releaseNamespace + "/");

        var releaseContent = release.Content as NodeTypeRelease;
        releaseContent.Should().NotBeNull("the Release MeshNode must carry a NodeTypeRelease payload");
        releaseContent!.NodeTypePath.Should().Be(NodeTypePath);
        releaseContent.Status.Should().Be("Succeeded");
        releaseContent.AssemblyPath.Should().NotBeNullOrEmpty();
        releaseContent.Notes.Should().NotBeNull();
        releaseContent.Notes!.Content.Should().Contain("First release of Sample");
        releaseContent.Notes.Content.Should().Contain("Bold");

        // 4. The NodeType's LatestReleasePath should point at the new release.
        // ObserveQuery the NodeType so we wait for the workspace → catalog
        // propagation rather than racing it with a single QueryAsync read.
        var defAfter = await meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{NodeTypePath}"))
            .Select(change => change.Items.FirstOrDefault()?.Content as NodeTypeDefinition)
            .Where(d => d?.LatestReleasePath == release.Path)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .ToTask(ct);
        defAfter.Should().NotBeNull();
        defAfter!.LatestReleasePath.Should().Be(release.Path);
        defAfter.CompilationStatus.Should().Be(CompilationStatus.Ok);
        // Notes are cleared from the NodeType after the release captures them
        // (immutable history).
        defAfter.ReleaseNotes.Should().BeNullOrEmpty(
            "the notes are now on the Release MeshNode itself; the NodeType's field is reset for the next release");
    }
}
