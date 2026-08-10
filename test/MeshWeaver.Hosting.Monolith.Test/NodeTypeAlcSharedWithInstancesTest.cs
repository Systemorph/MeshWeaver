using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A NodeType's collectible <c>NodeAssemblyLoadContext</c> is shared by every INSTANCE hub of
/// that type — so it must not be unloaded while any of them is still running its compiled code.
///
/// <para>The unload hook in <c>MeshDataSource.SubscribeToOwnDeletion</c> was written as if the ALC
/// had a per-NODE lifetime: when a node hub disposes it calls
/// <c>ICompilationCacheService.UnloadNodeContexts(SanitizeNodeName(hub.Address.Path))</c>, which
/// unloads every context registered under that name. For a NodeType hub (<c>type/X</c>) that is
/// every context holding <c>X</c>'s compiled assembly — including the one the live instance hubs of
/// <c>X</c> are executing. The NodeType hub disposes routinely: idle eviction, an explicit recycle,
/// or the restart after a recompile.</para>
///
/// <para>The damage is not theoretical and not only the documented use-after-unload
/// (<c>AccessViolationException</c>, issue #613). <c>TypeRegistry</c> subscribes to each collectible
/// context's <c>Unloading</c> and drops every entry belonging to it (it must — a registry walk over
/// freed metadata is the CI core dump that added the eviction). Unloading a context whose types a
/// live hub still uses therefore leaves that hub with a <c>DataContext</c> whose type sources
/// resolve fine (it holds the <see cref="Type"/> handles directly) and a <c>TypeRegistry</c> that
/// has forgotten them. The next <c>IWorkspace.GetStream&lt;T&gt;()</c> passes the type-source check
/// and then throws <c>ArgumentException "Type T is unknown."</c> from
/// <c>Workspace.GetStream(params Type[])</c> — permanently, until that instance hub is recycled.
/// That is the live failure this test was written from: every <c>/Store</c> page on every portal
/// rendering "⚠️ This area failed to render — Type StorePackage is unknown.", cured by recycling
/// the Store node and back on the next NodeType-hub disposal.</para>
///
/// <para>The probe NodeType mirrors the Store's shape exactly: a virtual data source over a type
/// declared in its own compiled sources, plus a layout area that reads it through
/// <c>host.Workspace.GetStream&lt;T&gt;()</c>.</para>
/// </summary>
public class NodeTypeAlcSharedWithInstancesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeTypeId = "AlcShareProbe";
    private const string ProbeArea = "Probe";

    /// <summary>The marker the area renders, with the count the virtual data source supplies.</summary>
    private const string ProbeMarker = "probe-packages:";

    private static string NodeTypePath => $"type/{NodeTypeId}";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// The NodeType's compiled sources: the virtual entity, the content type, the stream provider
    /// and the area that reads the virtual collection back out of the workspace. Deliberately the
    /// Store/Catalog shape — <c>WithVirtualType&lt;T&gt;</c> + <c>Workspace.GetStream&lt;T&gt;()</c>
    /// — because that is the pair the registry eviction breaks.
    /// </summary>
    private const string ProbeSource = """
        using System;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using System.Linq;
        using System.Reactive.Linq;
        using MeshWeaver.Data;
        using MeshWeaver.Domain;
        using MeshWeaver.Layout;
        using MeshWeaver.Layout.Composition;

        public record ProbePackage
        {
            [Key]
            public string Path { get; init; } = string.Empty;
        }

        public record AlcShareProbeContent
        {
            public string Title { get; init; } = string.Empty;
        }

        public static class AlcShareProbeAreas
        {
            public static IObservable<IEnumerable<ProbePackage>> Packages(IWorkspace workspace) =>
                Observable.Return<IEnumerable<ProbePackage>>(
                    ImmutableList.Create(
                        new ProbePackage { Path = "one" },
                        new ProbePackage { Path = "two" }));

            public static IObservable<UiControl?> Probe(LayoutAreaHost host, RenderingContext _) =>
                (host.Workspace.GetStream<ProbePackage>() ?? Observable.Return<ProbePackage[]?>(null))
                    .Select(list => (UiControl?)Controls.Markdown(
                        $"probe-packages:{(list is null ? -1 : list.Length)}"));
        }
        """;

    private const string ProbeConfiguration =
        "config => config.WithContentType<AlcShareProbeContent>()"
        + ".AddData(data => data.WithVirtualDataSource(\"probe-packages\", "
        + "vs => vs.WithVirtualType<ProbePackage>(AlcShareProbeAreas.Packages)))"
        + ".AddLayout(layout => layout.WithView(\"" + ProbeArea + "\", AlcShareProbeAreas.Probe))";

    /// <summary>
    /// Creates the NodeType + its Source Code node and waits for the terminal
    /// <c>CompilationStatus</c> the per-NodeType hub writes back onto its own MeshNode via
    /// <c>stream.Update</c> — the canonical compile-result channel (no verb-shaped compile request;
    /// see <c>NodeTypeAssemblyLeakTest</c> for why that response can simply never arrive).
    /// </summary>
    private async Task CompileProbeNodeTypeAsync()
    {
        var typeNode = MeshNode.FromPath(NodeTypePath) with
        {
            Name = NodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Configuration = ProbeConfiguration },
            State = MeshNodeState.Active,
        };

        await MeshService.CreateNode(typeNode)
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("code", $"{NodeTypePath}/Source")
            {
                NodeType = "Code",
                Name = "code",
                Content = new CodeConfiguration { Code = ProbeSource, Language = "csharp" },
                State = MeshNodeState.Active,
            }))
            .Should().Within(30.Seconds()).Emit();

        var compiledNode = await Mesh.GetMeshNodeStream(NodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition def
                        && def.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);

        var compiledDef = (NodeTypeDefinition)compiledNode.Content!;
        compiledDef.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the probe NodeType must compile; error: {compiledDef.CompilationError}");
    }

    /// <summary>
    /// Renders the probe area on the instance and returns the raw JSON of the rendered store.
    /// A distinct <c>Id</c> per call makes each one its OWN <c>LayoutAreaReference</c>, so the
    /// second render is a genuinely fresh evaluation of the view rather than a replay of the
    /// snapshot the first one left behind.
    /// </summary>
    private async Task<string> RenderProbeAsync(IMessageHub client, string instancePath, string renderId)
    {
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(instancePath),
            new LayoutAreaReference(ProbeArea) { Id = renderId });

        // The first emissions are the layout-progress placeholder ("Building layout…"), so asserting
        // on Emit() would assert on a spinner. Wait for the emission that carries the view's own
        // verdict — the rendered marker, or the error control the render-failure path substitutes.
        var value = await stream.Should().Within(30.Seconds()).Match(v =>
        {
            var text = v.Value.GetRawText();
            return text.Contains(ProbeMarker, StringComparison.Ordinal)
                   || text.Contains("failed to render", StringComparison.Ordinal);
        });

        var rendered = value.Value.GetRawText();
        Output.WriteLine($"[diag] render '{renderId}': {rendered}");
        return rendered;
    }

    /// <summary>
    /// Disposing the NodeType hub must not break the instance hubs that are still running its
    /// compiled code. RED before the fix: the second render answers
    /// "Type ProbePackage is unknown." because the instance hub's TypeRegistry was evicted when the
    /// shared ALC unloaded underneath it.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task InstanceHub_KeepsRendering_AfterItsNodeTypeHubDisposes()
    {
        await CompileProbeNodeTypeAsync();

        var instancePath = $"{TestPartition}/alc-share-instance";
        await NodeFactory.CreateNode(new MeshNode("alc-share-instance", TestPartition)
        {
            Name = "Alc Share Instance",
            NodeType = NodeTypePath,
            State = MeshNodeState.Active,
        }).Should().Emit();

        var client = GetClient(c => c.AddData(data => data));

        var before = await RenderProbeAsync(client, instancePath, "first");
        before.Should().Contain($"{ProbeMarker}2",
            "the virtual data source feeds the area through Workspace.GetStream<ProbePackage>()");

        // The instance hub is now live and holds the NodeType's compiled types. Dispose ONLY the
        // NodeType hub — the everyday event this guards: idle eviction, an explicit recycle, or the
        // hub restart after a recompile. The mesh and the instance hub keep running.
        var nodeTypeHub = Mesh.GetHostedHub(Mesh.GetAddress(NodeTypePath), HostedHubCreation.Never);
        nodeTypeHub.Should().NotBeNull("compiling the NodeType activates its per-node hub");
        nodeTypeHub!.Dispose();

        await nodeTypeHub.DisposalCompleted
            .FirstOrDefaultAsync()
            .Timeout(30.Seconds())
            .ToTask();

        var after = await RenderProbeAsync(client, instancePath, "second");

        after.Should().NotContain("is unknown",
            "the instance hub still executes the NodeType's compiled types, so its collectible "
            + "AssemblyLoadContext may not be unloaded — unloading it evicts those types from the "
            + "TypeRegistry and every Workspace.GetStream<T>() on that hub throws "
            + "\"Type T is unknown.\" for the rest of the hub's life (the /Store page outage)");
        after.Should().Contain($"{ProbeMarker}2",
            "the area must render exactly as it did before its NodeType hub went away");
    }
}
