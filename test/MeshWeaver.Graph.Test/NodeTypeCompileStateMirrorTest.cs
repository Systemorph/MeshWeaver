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
/// #5389: a NodeType hub's activation and a later change of its compile state write NO
/// <c>{type}/_Activity/compile-state</c> satellite. The phase-1 dual-write of issue #748 did, on
/// every activation — one node-operation round trip per NodeType, and one extra grain activation
/// per NodeType whenever the state moved — for a record nothing reads. On memex-cloud those
/// satellites were the destinations of the boot-time placement timeouts (#5334) and stalled
/// activations (#5531). Doc/Architecture/CompileStateSatelliteRetired carries the measurement.
///
/// <para>Runs on a REAL mesh through the full activation path (the per-node hub setup in
/// <c>MeshDataSource</c> is where the mirror used to be installed), with a positive control on
/// each side: the change the mirror used to follow DOES land on the NodeType node, and the
/// instrument that reads the satellite DOES see a satellite that exists — so "absent" below is a
/// reading, not a blind spot.</para>
/// </summary>
public class NodeTypeCompileStateMirrorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "MirrorSpace";
    private const string TypePath = $"{Space}/Widget";
    private const string ControlTypePath = $"{Space}/Gadget";

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
    public async Task NodeTypeActivationAndStateChange_WriteNoCompileStateSatellite()
    {
        var ct = TestContext.Current.CancellationToken;
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
            }).Should().Emit(cancellationToken: ct);

        // Activate the type's own hub — the mirror installed on per-node hub activation and wrote
        // its first observed state unconditionally, so this alone used to produce the satellite.
        await ReadNode(TypePath).FirstAsync().Timeout(30.Seconds()).Await(ct);

        // A state CHANGE on the node — the second thing the mirror used to follow.
        using (accessService.ImpersonateAsSystem())
        {
            var current = await ReadNode(TypePath).FirstAsync().Timeout(30.Seconds()).Await(ct);
            Assert.NotNull(current);
            var content = current.ContentAs<NodeTypeDefinition>(options)!;
            await meshService.UpdateNode(current with
            {
                Content = content with
                {
                    LastCompiledVersion = 2026,
                    LatestAssemblyPath = "Widget/v2026.dll",
                },
            }).Should().Emit(cancellationToken: ct);
        }

        // POSITIVE CONTROL 1: the change landed on the NodeType node itself — the one record every
        // compile gate and view reads. So the hub was live and the state really moved.
        var landed = await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => ReadNode(TypePath))
            .Select(n => n?.ContentAs<NodeTypeDefinition>(options))
            .Where(d => d is { LastCompiledVersion: 2026, LatestAssemblyPath: "Widget/v2026.dll" })
            .FirstAsync()
            .Timeout(60.Seconds())
            .Await(ct);
        Assert.NotNull(landed);

        // POSITIVE CONTROL 2: the instrument sees a satellite that DOES exist. Written directly for
        // a different type, so it cannot be mistaken for anything the activation path produced.
        var controlStatePath = NodeTypeCompileStateMirror.StatePath(ControlTypePath);
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(NodeTypeCompileStateMirror.StateNode(
                    ControlTypePath,
                    new NodeTypeCompileState { LastCompiledVersion = 7 },
                    options))
                .Should().Emit(cancellationToken: ct);
        // Read through the SAME instrument the negative below uses — a scope:children listing of the
        // `_Activity` parent — so "absent" there is a reading of an instrument shown to see one.
        var controlListed = await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => SatelliteListing(meshService, ControlTypePath))
            .Where(paths => paths.Contains(controlStatePath))
            .FirstAsync()
            .Timeout(30.Seconds())
            .Await(ct);
        Assert.Contains(controlStatePath, controlListed);

        // THE ASSERTION: no satellite for the activated, changed type. Existence is read off a
        // scope:children listing, never a point read of a node expected NOT to exist (that opens the
        // missing-node breaker on the path). A negative with no positive signal to wait for, so it is
        // read over a bounded window — the mirror used to write within milliseconds of the
        // activation above, well inside it.
        var statePath = NodeTypeCompileStateMirror.StatePath(TypePath);
        var satellite = await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .Take(20)
            .SelectMany(_ => SatelliteListing(meshService, TypePath))
            .Where(paths => paths.Contains(statePath))
            .FirstOrDefaultAsync()
            .Await(ct);
        Assert.True(satellite is null,
            $"a NodeType activation must not write {statePath} (#5389) — nothing reads it, and the "
            + "write cost a node-operation round trip and a grain activation per NodeType");
    }

    /// <summary>The paths listed directly under <c>{typePath}/_Activity</c> — one Initial snapshot of
    /// a scope:children query, the sanctioned existence read for a node that may not exist.</summary>
    private static IObservable<IReadOnlyList<string>> SatelliteListing(IMeshService meshService, string typePath) =>
        meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{typePath}/_Activity scope:children"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(c => (IReadOnlyList<string>)c.Items.Select(n => n.Path).ToArray());
}
