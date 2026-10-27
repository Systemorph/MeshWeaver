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
    public async Task TheSameBuildAtANewerGeneration_IsStillWritten_SoADelayedMiddleBuildCannotReplaceIt()
    {
        // Build A stored at generation 1; build B's generation-2 edge write is delayed; A is published
        // again at generation 3 (same bytes, same edges). The idempotency shortcut must not keep
        // generation 1 — otherwise B's delayed generation-2 write would replace A's edges.
        var ct = TestContext.Current.CancellationToken;
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
        await Write(Mesh, Set(1, a, "Lib/A"), ct);
        await Write(Mesh, Set(3, a, "Lib/A"), ct);
        (await Stored(ct))!.StampVersion.Should().Be(3L, "the same build at a newer generation advances the generation");

        await Write(Mesh, Set(2, "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", "Lib/B"), ct);
        var stored = await Stored(ct);
        stored!.BuildMvid.Should().Be(a, "the delayed middle generation is older than the stored one");
        stored.Edges.Select(e => e.Target).Should().Equal(["Lib/A"]);
    }

    [Fact]
    public void DescribesSameBuild_SkipsOnlyWhenTheStoredGenerationIsNotOlder()
    {
        DependencyEdgeSet At(long? generation) => new()
        {
            Source = "x",
            BuildMvid = "a",
            StampVersion = generation,
            Edges = [new DependencyEdge { Target = "Lib/A", Kind = DependencyEdgeKinds.NodeType }],
        };

        DependencyEdgeSetWriter.DescribesSameBuild(At(3), At(3)).Should().BeTrue("same build, same generation");
        DependencyEdgeSetWriter.DescribesSameBuild(At(4), At(3)).Should().BeTrue("an older incoming generation writes nothing anyway");
        DependencyEdgeSetWriter.DescribesSameBuild(At(1), At(3)).Should().BeFalse("a newer generation must be persisted");
        DependencyEdgeSetWriter.DescribesSameBuild(At(null), At(3)).Should().BeFalse("a generation where none is stored must be persisted");
        DependencyEdgeSetWriter.DescribesSameBuild(At(3), At(null)).Should().BeTrue();
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
    public void ASettleWhoseStampUpdateWasAGuardedNoOp_PublishesNoEdges()
    {
        // The stamp lambda returns the node unchanged when its content stopped being a
        // NodeTypeDefinition mid-compile; the saved node's version then belongs to whatever the node
        // holds now, and the compile's edges must not be published under it.
        var edges = Set(null, "dddddddddddddddddddddddddddddddd", "Lib/X");
        var saved = MeshNode.FromPath(SourcePath) with { Version = 9 };

        DependencyEdgeSetWriter.ToPublish(edges, successStamped: false, saved).Should().BeNull(
            "a no-op stamp names no build of this compile");
        DependencyEdgeSetWriter.ToPublish(null, successStamped: true, saved).Should().BeNull("no edges, nothing to publish");

        // Control: the stamp that landed publishes the edges under the version it landed at.
        var published = DependencyEdgeSetWriter.ToPublish(edges, successStamped: true, saved);
        published.Should().NotBeNull();
        published!.StampVersion.Should().Be(9L);
        published.BuildMvid.Should().Be(edges.BuildMvid);
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
