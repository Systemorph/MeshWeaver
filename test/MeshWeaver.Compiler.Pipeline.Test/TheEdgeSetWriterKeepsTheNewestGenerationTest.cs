using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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

    private async Task<DependencyEdgeSet?> Stored()
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = await storage.Read(DependencyEdgeSetPaths.PathFor(SourcePath), Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the store answers", TestContext.Current.CancellationToken);
        return node?.ContentAs<DependencyEdgeSet>(Mesh.JsonSerializerOptions);
    }

    private Task Write(DependencyEdgeSet set) =>
        DependencyEdgeSetWriter.Write(Mesh, set, null)
            .ToList()
            .Should().Within(TestTimeouts.Convergence).Emit("the write completes", TestContext.Current.CancellationToken);

    [Fact(Timeout = 120_000)]
    public async Task AnOlderGenerationArrivingLast_DoesNotReplaceTheNewerBuildsEdges()
    {
        // The reordered success: generation 7 (newer build) is written first, generation 6 (the
        // older compile whose edge write was delayed) arrives after it.
        await Write(Set(7, "77777777777777777777777777777777", "Lib/New"));
        await Write(Set(6, "66666666666666666666666666666666", "Lib/Old"));

        var stored = await Stored();
        stored.Should().NotBeNull();
        stored!.StampVersion.Should().Be(7L, "the older generation must not overwrite the newer one");
        stored.BuildMvid.Should().Be("77777777777777777777777777777777");
        stored.Edges.Select(e => e.Target).Should().Equal(["Lib/New"]);

        // A genuinely newer generation still replaces it whole.
        await Write(Set(8, "88888888888888888888888888888888", "Lib/Newer"));
        stored = await Stored();
        stored!.StampVersion.Should().Be(8L);
        stored.Edges.Select(e => e.Target).Should().Equal(["Lib/Newer"]);
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
