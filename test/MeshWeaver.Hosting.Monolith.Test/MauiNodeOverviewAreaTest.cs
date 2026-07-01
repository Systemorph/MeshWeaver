using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Narrows the native-MAUI "keystone". On-device (maccatalyst) a node page — even a pure-prose Markdown doc
/// (verified with "Deployment — AKS") — renders its title + metadata but its Overview CONTENT sub-areas spin
/// forever. This test drives the EXACT path the client uses (<c>NodeAreaView</c> →
/// <c>GetRemoteStream&lt;JsonElement, LayoutAreaReference&gt;((Address)nodePath, new LayoutAreaReference("Overview"))</c>)
/// against the full mesh with REAL per-node hubs, and asserts the Overview control AND each nested sub-area
/// resolve.
/// <para><b>Result: it PASSES</b> — a freshly-created Markdown node's Overview + sub-areas resolve fine over
/// the per-node-hub remote stream. So per-node-hub topology is NOT the cause (ruling it out is the point of
/// this test), and the earlier within-host data-path proofs stand. The on-device spinner must therefore be
/// specific to the MAUI CLIENT's environment — the leading suspect is a DOCUMENTATION node whose Overview
/// body is backed by a CONTENT COLLECTION (the offline SQLite client loads .md bodies via
/// <c>AddDocumentation</c>), not the bare inline-content node created here. The next repro should load a
/// content-collection-backed doc node under the client's config and open its Overview.</para>
/// </summary>
public class MauiNodeOverviewAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Namespace = $"{TestPartition}/MauiOverview";

    [Fact(Timeout = 90_000)]
    public async Task NodeOverview_TopControl_ResolvesOverRemoteStream()
    {
        var path = $"{Namespace}/doc1";
        await NodeFactory.CreateNode(new MeshNode("doc1", Namespace)
        {
            Name = "Doc One",
            NodeType = "Markdown",
        }).Should().Emit();

        var workspace = Mesh.GetWorkspace();
        // EXACTLY what NodeAreaView / the MAUI LayoutAreaView(remote) does for a node page.
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            (Address)path, new LayoutAreaReference("Overview"));

        var root = await stream.GetControlStream("Overview")
            .Where(c => c is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        root.Should().NotBeNull("the node Overview top control must render over the per-node-hub remote stream");
        Output.WriteLine($"Overview root: {root!.GetType().Name}");
    }

    [Fact(Timeout = 90_000)]
    public async Task NodeOverview_NestedSubAreas_ResolveOverRemoteStream()
    {
        var path = $"{Namespace}/doc2";
        await NodeFactory.CreateNode(new MeshNode("doc2", Namespace)
        {
            Name = "Doc Two",
            NodeType = "Markdown",
        }).Should().Emit();

        var workspace = Mesh.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            (Address)path, new LayoutAreaReference("Overview"));

        var root = await stream.GetControlStream("Overview")
            .Where(c => c is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        Output.WriteLine($"Overview root: {root!.GetType().Name}");

        // The MAUI RenderArea-per-child path: for every nested NamedAreaControl the container declares, the
        // view opens GetControlStream(child.Area) on the SAME stream. Each must resolve — a spinner-forever
        // is a child whose stream never emits.
        if (root is IContainerControl container)
        {
            var areas = container.Areas.ToList();
            Output.WriteLine($"Overview declares {areas.Count} sub-area(s): {string.Join(", ", areas.Select(a => a.Area))}");
            foreach (var named in areas)
            {
                var area = named.Area!.ToString()!;
                var sub = await stream.GetControlStream(area)
                    .Where(c => c is not null)
                    .FirstAsync().Timeout(30.Seconds()).ToTask();
                sub.Should().NotBeNull($"nested Overview sub-area '{area}' must resolve over the remote stream (not spin forever)");
                Output.WriteLine($"  {area} -> {sub!.GetType().Name}");
            }
        }
    }
}
