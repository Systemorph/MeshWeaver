using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// Two writers publish one source's edge set — the activation's compile settle and the batch
/// bake — each AFTER its own stamp landed (<c>Doc/Architecture/DependencyGraph</c>). An older
/// compile can land its stamp, pause, and arrive with its edges after a newer build has stamped and
/// written. The set's generation (<see cref="DependencyEdgeSet.StampVersion"/>, the record version
/// the stamp landed at) decides: a lower generation never replaces a higher one.
/// </summary>
public class TheEdgeSetWriterKeepsTheNewestGenerationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string SourcePath => $"{TestPartition}/Deps{suffix}";

    private DependencyEdgeSet Set(long? generation, string mvid, string target) => new()
    {
        Source = SourcePath,
        BuildMvid = mvid,
        StampVersion = generation,
        Edges = [new DependencyEdge { Target = target, Kind = DependencyEdgeKinds.NodeType }],
    };

    private async Task<DependencyEdgeSet?> Stored(CancellationToken ct)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = await storage.Read(DependencyEdgeSetPaths.PathFor(SourcePath), Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the store answers", ct);
        return node?.ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions);
    }

    private static Task Write(IMessageHub mesh, DependencyEdgeSet set, CancellationToken ct) =>
        DependencyEdgeSetWriter.Write(mesh, set, null)
            .ToList()
            .Should().Within(TestTimeouts.Convergence).Emit("the write completes", ct);

    [Fact(Timeout = 120_000)]
    public async Task AnOlderGenerationArrivingLast_DoesNotReplaceTheNewerBuildsEdges()
    {
        var ct = TestContext.Current.CancellationToken;
        // The reordered success: generation 7 (newer build) is written first, generation 6 (the
        // older compile whose edge write was delayed) arrives after it.
        await Write(Mesh, Set(7, "77777777777777777777777777777777", "Lib/New"), ct);
        await Write(Mesh, Set(6, "66666666666666666666666666666666", "Lib/Old"), ct);

        var stored = await Stored(ct);
        stored.Should().NotBeNull();
        stored!.StampVersion.Should().Be(7L, "the older generation must not overwrite the newer one");
        stored.BuildMvid.Should().Be("77777777777777777777777777777777");
        stored.Edges.Select(e => e.Target).Should().Equal(["Lib/New"]);

        // A genuinely newer generation still replaces it whole.
        await Write(Mesh, Set(8, "88888888888888888888888888888888", "Lib/Newer"), ct);
        stored = await Stored(ct);
        stored!.StampVersion.Should().Be(8L);
        stored.Edges.Select(e => e.Target).Should().Equal(["Lib/Newer"]);
    }

    [Fact(Timeout = 120_000)]
    public async Task TwoBuildsAtTheSameGeneration_TheOneTheRecordNames_WinsInEitherOrder()
    {
        // Both stamp writers can mint one version for two builds (the activation's unflushed v+1,
        // the batch bake's v+1 from the durable v). The record decides the tie, whichever order the
        // two edge writes arrive in.
        var ct = TestContext.Current.CancellationToken;
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        const string named = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        await storage.Write(MeshNode.FromPath(SourcePath) with
            {
                NodeType = MeshNode.NodeTypePath,
                Version = 5,
                Content = new NodeTypeDefinition { LatestAssemblyMvid = named },
            }, Mesh.JsonSerializerOptions).Await(ct);

        await Write(Mesh, Set(5, other, "Lib/Other"), ct);
        await Write(Mesh, Set(5, named, "Lib/Named"), ct);
        var stored = await Stored(ct);
        stored!.BuildMvid.Should().Be(named, "the later write is the build the record names");

        await Write(Mesh, Set(5, other, "Lib/Other"), ct);
        stored = await Stored(ct);
        stored!.BuildMvid.Should().Be(named, "a tie the record does not name never replaces the one it does");
        stored.Edges.Select(e => e.Target).Should().Equal(["Lib/Named"]);
    }

    [Fact]
    public void TheEdgeSetNode_IsASatelliteOfItsSource()
    {
        var node = DependencyEdgeSetWriter.NodeFor(Set(1, "cccccccccccccccccccccccccccccccc", "Lib/X"));
        node.Path.Should().Be(DependencyEdgeSetPaths.PathFor(SourcePath));
        node.MainNode.Should().Be(SourcePath, "the edge set must not answer is:main or list as an instance");
    }

    [Fact]
    public void IsTie_AndRecordNamesAnotherBuild()
    {
        DependencyEdgeSet At(long? generation, string mvid) => new() { Source = "x", StampVersion = generation, BuildMvid = mvid };

        DependencyEdgeSetWriter.IsTie(At(3, "a"), At(3, "b")).Should().BeTrue();
        DependencyEdgeSetWriter.IsTie(At(3, "a"), At(3, "A")).Should().BeFalse("the same build re-writes");
        DependencyEdgeSetWriter.IsTie(At(3, "a"), At(4, "b")).Should().BeFalse("a newer generation is no tie");
        DependencyEdgeSetWriter.IsTie(null, At(3, "b")).Should().BeFalse();

        DependencyEdgeSetWriter.RecordNamesAnotherBuild(new NodeTypeDefinition { LatestAssemblyMvid = "a" }, At(3, "b")).Should().BeTrue();
        DependencyEdgeSetWriter.RecordNamesAnotherBuild(new NodeTypeDefinition { LatestAssemblyMvid = "b" }, At(3, "b")).Should().BeFalse();
        DependencyEdgeSetWriter.RecordNamesAnotherBuild(new NodeTypeDefinition(), At(3, "b")).Should().BeFalse("an in-memory record names no build");
        DependencyEdgeSetWriter.RecordNamesAnotherBuild(null, At(3, "b")).Should().BeFalse();
    }

    [Fact]
    public void IsSupersededBy_OnlyAStrictlyNewerStoredGenerationWins()
    {
        DependencyEdgeSet At(long? generation) => new() { Source = "x", StampVersion = generation };

        DependencyEdgeSetWriter.IsSupersededBy(null, At(1)).Should().BeFalse("nothing stored yet");
        DependencyEdgeSetWriter.IsSupersededBy(At(null), At(1)).Should().BeFalse("a set with no generation is replaced");
        DependencyEdgeSetWriter.IsSupersededBy(At(3), At(3)).Should().BeFalse("the same stamp re-writes");
        DependencyEdgeSetWriter.IsSupersededBy(At(3), At(4)).Should().BeFalse("a newer generation replaces");
        DependencyEdgeSetWriter.IsSupersededBy(At(4), At(3)).Should().BeTrue("an older generation never does");
        DependencyEdgeSetWriter.IsSupersededBy(At(4), At(null)).Should().BeTrue("an ungenerated set never replaces a generated one");
    }
}
