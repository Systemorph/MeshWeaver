using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The image-bundle pass skips an entry whose build the record already serves (the store holds its
/// bytes under that record) BEFORE any payload is read — so that entry never reaches the seeding
/// hook. A build adopted before its bundle carried dependency edges (every type on the production
/// meshes that motivated <c>Doc/Architecture/DependencyGraph</c> → adopted prebuilts), or whose
/// first edge write failed, must still get its set from a bundle that now carries one.
///
/// <para>Control: the first pass's bundle carries NO edges, so the set that appears after the second
/// pass can only have come from the already-current entry.</para>
/// </summary>
public class AnAlreadyCurrentBundleEntryBackfillsItsEdgesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "mw-edges-backfill-" + Guid.NewGuid().ToString("N")[..12]);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static byte[] AssemblyBytes() =>
        File.ReadAllBytes(typeof(AnAlreadyCurrentBundleEntryBackfillsItsEdgesTest).Assembly.Location);

    private string StageBundle(string folder, string typePath, JsonElement? edges)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        using (var file = File.Create(Path.Combine(dir, "widget.zip")))
            BundleWriter.Write(
                file, plugin: "Widget", version: "1.0.0",
                frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
                assemblies:
                [
                    new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream(AssemblyBytes()))
                    {
                        DependencyEdges = edges,
                    },
                ]);
        return dir;
    }

    private static JsonElement EdgesJson(string source, string mvid) =>
        JsonSerializer.SerializeToElement(new DependencyEdgeSet
        {
            Source = source,
            BuildMvid = mvid,
            Edges = [new DependencyEdge { Target = "Lib/Used", Kind = DependencyEdgeKinds.NodeType }],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>The NodeType catalog query once it reports <paramref name="predicate"/> for the type —
    /// the pass decides "already current" from that query, so the test waits for it rather than racing it.</summary>
    private Task CatalogShows(string typePath, Func<NodeTypeDefinition?, bool> predicate, string because, CancellationToken ct) =>
        Observable.Timer(TimeSpan.Zero, TimeSpan.FromMilliseconds(100))
            .SelectMany(_ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath))).Take(1))
            .Where(change => change.Items.Any(n => string.Equals(n.Path, typePath, StringComparison.OrdinalIgnoreCase)
                && predicate(n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))))
            .Take(1)
            .Should().Within(TestTimeouts.CrossSilo).Emit(because, ct);

    [Fact(Timeout = 180_000)]
    public async Task AnAlreadyAdoptedBuild_GetsTheEdgesItsBundleNowCarries()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = $"{TestPartition}/Backfill{Guid.NewGuid().ToString("N")[..8]}";
        var mvid = ServedBuildIdentity.OfBytes(AssemblyBytes())!;
        try
        {
            await MeshService.CreateNode(MeshNode.FromPath(typePath) with
                {
                    Name = typePath,
                    NodeType = MeshNode.NodeTypePath,
                    State = MeshNodeState.Active,
                    Content = new NodeTypeDefinition { Description = "backfill target", Configuration = "config => config" },
                })
                .Should().Within(TestTimeouts.Convergence).Emit("the NodeType must exist", ct);
            await CatalogShows(typePath, _ => true, "the pass enumerates the catalog", ct);

            // Pass 1 — a bundle from a producer that recorded no edges: adopted, no edge set.
            (await ShippedPrebuiltBundles.SeedAll(Mesh, StageBundle("before", typePath, edges: null), null)
                    .Should().Within(TestTimeouts.CrossSilo).Emit("the first pass completes", ct))
                .Should().Be(1, "the premise: the first pass adopts the bytes");
            await CatalogShows(typePath,
                d => string.Equals(d?.LatestAssemblyMvid, mvid, StringComparison.OrdinalIgnoreCase)
                     && d?.LastCompiledVersion is >= 0 && d.RequestedSourceStampAt is null,
                "the catalog reports the adopted build, so the next pass finds the entry already current", ct);
            (await Storage.Read(DependencyEdgeSetPaths.PathFor(typePath), Mesh.JsonSerializerOptions).Take(1)
                    .Should().Within(TestTimeouts.Convergence).Emit("the store answers", ct))
                .Should().BeNull("the premise: a bundle without edges writes none");

            // Pass 2 — the same bytes, now carrying their edges. The entry is already current.
            await ShippedPrebuiltBundles.SeedAll(Mesh, StageBundle("after", typePath, EdgesJson(typePath, mvid)), null)
                .Should().Within(TestTimeouts.CrossSilo).Emit("the second pass completes", ct);

            var row = await Observable.Timer(TimeSpan.Zero, TimeSpan.FromMilliseconds(50))
                .SelectMany(_ => Storage.Read(DependencyEdgeSetPaths.PathFor(typePath), Mesh.JsonSerializerOptions).Take(1))
                .Where(n => n is not null)
                .Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("an already-current entry still writes the edges its bundle carries", ct);
            var set = row!.ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions)!;
            set.BuildMvid.Should().Be(mvid);
            set.Edges.Select(e => e.Target).Should().Equal(["Lib/Used"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
