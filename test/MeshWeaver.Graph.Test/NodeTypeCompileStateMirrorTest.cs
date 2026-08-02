using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The phase-1 dual-write of issue #748 on a REAL mesh: a NodeType node's operational compile
/// members are mirrored onto the fixed-id satellite at <c>{type}/_Activity/compile-state</c>,
/// and a later change of the node's state updates the satellite. The mirror is installed by the
/// per-node hub setup (<c>MeshDataSource</c>), so this exercises the full activation path —
/// not the projection in isolation.
/// </summary>
public class NodeTypeCompileStateMirrorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "MirrorSpace";
    private const string TypePath = $"{Space}/Widget";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Mirror Space", NodeType = "Space" });

    private static JsonObject WidgetContent(long compiledVersion) =>
        JsonNode.Parse(
            $$"""
            {"$type":"NodeTypeDefinition","description":"widget type",
             "configuration":"config => config",
             "lastCompiledVersion":{{compiledVersion}},
             "latestAssemblyPath":"Widget/v{{compiledVersion}}.dll"}
            """)!.AsObject();

    [Fact(Timeout = 120000)]
    public async Task OperationalState_LandsOnTheSatellite_AndFollowsChanges()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var options = Mesh.JsonSerializerOptions;

        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode("Widget", Space)
            {
                NodeType = MeshNode.NodeTypePath,
                Name = "Widget",
                State = MeshNodeState.Active,
                Content = WidgetContent(1082),
            }).Should().Emit();

        // The mirror (installed by the per-node hub's activation) lands the state on the
        // fixed-id satellite. Compile machinery may add its own flips (status kickoffs);
        // the assembly pointer we seeded is not written by any failure path, so it is the
        // stable assertion target.
        var statePath = NodeTypeCompileStateMirror.StatePath(TypePath);
        // Activate the type's own hub: the mirror installs on per-node hub activation, and a
        // bare create in this fixture does not spin the hub up. In production type hubs
        // activate constantly (compiles, instance resolution, the PreWarm sweep); the
        // authoritative owner round-trip is the same touch.
        await ReadNode(TypePath).FirstAsync().Timeout(30.Seconds());
        await WaitForState(statePath, s =>
            s.LastCompiledVersion == 1082 && s.LatestAssemblyPath == "Widget/v1082.dll");

        // A state CHANGE on the node follows onto the satellite.
        using (accessService.ImpersonateAsSystem())
        {
            var current = await ReadNode(TypePath).FirstAsync().Timeout(30.Seconds());
            var content = current!.ContentAs<NodeTypeDefinition>(options)!;
            await meshService.UpdateNode(current with
            {
                Content = content with
                {
                    LastCompiledVersion = 2026,
                    LatestAssemblyPath = "Widget/v2026.dll",
                },
            }).Should().Emit();
        }

        await WaitForState(statePath, s =>
            s.LastCompiledVersion == 2026 && s.LatestAssemblyPath == "Widget/v2026.dll");
    }

    /// <summary>Polls the satellite until its parsed state satisfies <paramref name="predicate"/> —
    /// the mirror writes asynchronously, so a single read can race it.</summary>
    private async Task WaitForState(string statePath, Func<NodeTypeCompileState, bool> predicate) =>
        await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => ReadNode(statePath)
                .Catch((Exception _) => Observable.Return<MeshNode?>(null)))
            .Select(n => NodeTypeCompileStateMirror.Parse(n, Mesh.JsonSerializerOptions))
            .Where(s => s is not null && predicate(s))
            .FirstAsync()
            .Timeout(60.Seconds());
}
