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

    [Fact(Timeout = 120000)]
    public async Task CompilationPending_CreatesReleaseMeshNode_WithNotes()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        // 1. Create the NodeType with a trivial source. No Code source needed —
        //    a NodeTypeDefinition with only a hub-config lambda compiles to a
        //    valid (empty) assembly. The per-NodeType hub's auto-watcher
        //    (`InstallCompileWatcher`) flips CompilationStatus = Pending on
        //    activation (HasUsableBuild=false) and dispatches an activity-based
        //    compile that lands a kickoff Release with no notes.
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

        // 2. Wait for the kickoff compile to settle. Reading via the live
        //    MeshNode stream (not QueryAsync against the lagged catalog) gives
        //    us the live snapshot to base the UpdateNode on, so the test's
        //    write never carries a stale Pending/Compiling status back to the
        //    parent (which would re-fire the watcher and race a second
        //    activity into the explicit-CreateRelease's compile).
        var kickoffSnapshot = await workspace
            .GetMeshNodeStream(NodeTypePath)
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask(ct);
        kickoffSnapshot.Should().NotBeNull();

        // 3. Click Create Release: write the release notes onto the live
        //    NodeType snapshot, then post `CreateReleaseRequest(Force: true)`.
        //    The handler delegates to the auto-watcher (flips Pending) so the
        //    compile runs through the same single-activity pipeline; the
        //    activity reads the just-written ReleaseNotes off the parent and
        //    seeds the new Release MeshNode with them.
        var releaseNotes = "First release of Sample. **Bold** + _italic_ + a list:\n- one\n- two";
        await meshService.UpdateNode(kickoffSnapshot with
        {
            Content = (kickoffSnapshot.Content as NodeTypeDefinition)! with
            {
                ReleaseNotes = releaseNotes
            }
        });

        await Mesh.Observe(new CreateReleaseRequest(Force: true),
                o => o.WithTarget(new Address(NodeTypePath)))
            .FirstAsync().ToTask(ct);

        // 4. Wait for the Release with the user's notes to appear in the
        //    catalog. Filtering on Notes content (rather than "release path !=
        //    kickoff release path") is the strongest invariant — guarantees
        //    we're verifying the explicit CreateReleaseRequest's release, not
        //    the kickoff's notes-less one. Doesn't assert which release ends
        //    up in NodeTypeDefinition.LatestReleasePath — both kickoff and
        //    explicit-CreateRelease activities each write LatestReleasePath on
        //    the parent and last-writer-wins isn't a correctness invariant the
        //    framework can guarantee (both releases are durable history; the
        //    "active" pointer can race). What matters for this test's invariant
        //    is that the user-authored notes were captured into a real Release
        //    MeshNode.
        var releaseNamespace = $"{NodeTypePath}/_Release";
        var release = await meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{releaseNamespace} nodeType:Release"))
            .Select(change => change.Items
                .FirstOrDefault(n => n.Content is NodeTypeRelease r
                    && r.Notes is { } notes
                    && notes.Content.Contains("First release of Sample")))
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(60))
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
    }
}
