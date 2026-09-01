using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A SOURCE THAT SHIPS AN INSTANCE OF A TYPE IT INTRODUCES MUST LAND IN ONE PASS (issue #2556).
///
/// <para>The create pipeline refuses a node whose <c>NodeType</c> names nothing the mesh knows:
/// <c>Upsert of '…' failed: NodeType 'X' is not registered</c> (MeshExtensions step 3 — the static
/// node registry, else <c>IStorageAdapter.Exists(typePath)</c>). The importer used to write the
/// source's nodes in whatever order the source enumerated them, five at a time, so an instance that
/// happened to precede its type node was refused — and the <c>#2229</c> baseline guard then held the
/// sync baseline so the pass would be RETRIED, with the same ordering, forever. memex-cloud measured
/// 6,902 refusals in 90 minutes and one node refused 40 times in 120: a loop that cannot converge,
/// because every attempt fails identically.</para>
///
/// <para>What is asserted is CONVERGENCE IN ONE PASS, not a log string: the instance is present and
/// the import reports no failures. Before the fix this import returned
/// <c>ImportedWithErrors</c> and the instance was simply absent.</para>
/// </summary>
public class ImportTypeBeforeInstanceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The instance is enumerated FIRST and its type LAST, past the importer's concurrency window
    /// (<c>Merge(5)</c>) — so without an ordering pass the instance is provably written before the
    /// type node exists.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task InstanceEnumeratedBeforeItsType_StillLandsInOnePass()
    {
        var partition = "Tb" + Guid.NewGuid().ToString("N")[..8];
        var typePath = $"{partition}/Widget";

        var nodes = new List<MeshNode> { Instance(partition, "Inst", typePath) };
        // Filler: pushes the type node past Merge(BatchSize=5), which subscribes the first five
        // immediately and the sixth only once one of them has completed. The instance is therefore
        // attempted-and-refused before the type node is even subscribed.
        nodes.AddRange(Enumerable.Range(0, 12).Select(i => Page(partition, $"Filler{i}")));
        nodes.Add(TypeNode(partition, "Widget"));

        var source = new FakeRepoSource(partition) { Root = Space(partition), Nodes = nodes };

        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(180.Seconds());
        Output.WriteLine(
            $"outcome={result.Outcome} count={result.Count} failed={result.Failed} "
            + $"blocked=[{string.Join(", ", result.BlockedCreatePaths)}]");

        result.Failed.Should().Be(0,
            "the source ships the type it uses, so ONE pass must land everything — a refused instance "
            + "holds the sync baseline and the retry re-runs the identical ordering forever (#2556)");
        result.Outcome.Should().Be("Imported");
        (await Body($"{partition}/Inst")).Should().Contain("instance",
            "the typed instance must exist after the import that also introduced its type");
    }

    /// <summary>
    /// The complement that keeps the fix honest: a node whose type NO source in this pass carries and
    /// which the mesh does not know is NOT a failure the importer can order away. It is reported as a
    /// BLOCKED CREATE — named, Warning, and deliberately NOT counted as <c>Failed</c>, so one
    /// unsatisfiable node cannot freeze a Space's whole sync baseline (the shape that turned a single
    /// refusal into a permanent loop). Everything else in the same pass still lands.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ForeignType_IsReportedAsBlocked_NotAsAFailureThatHoldsTheBaseline()
    {
        var partition = "Tf" + Guid.NewGuid().ToString("N")[..8];
        var source = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes =
            [
                Instance(partition, "Orphan", "Some/Other/Partition/Widget"),
                Page(partition, "Fine"),
            ],
        };

        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(180.Seconds());
        Output.WriteLine(
            $"outcome={result.Outcome} count={result.Count} failed={result.Failed} "
            + $"blocked=[{string.Join(", ", result.BlockedCreatePaths)}]");

        result.BlockedCreatePaths.Should().Contain($"{partition}/Orphan",
            "a type carried by no source and absent from the mesh cannot be ordered into existence — "
            + "it must be NAMED so an operator can act, not retried silently forever");
        result.Failed.Should().Be(0,
            "a blocked create is drift the import cannot close; counting it as Failed holds the git "
            + "baseline and freezes every LATER commit of the same repo too");
        result.Outcome.Should().Be("ImportedWithBlockedCreates");
        (await Body($"{partition}/Fine")).Should().Contain("page",
            "one unsatisfiable node must not stop the rest of the pass");
    }

    private async Task<string> Body(string path)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(60.Seconds());
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = "Type Ordering", NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Type Ordering\n\nfixture." }
    };

    private static MeshNode Page(string partition, string id) => new(id, partition)
    {
        NodeType = "Markdown", Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}\n\npage" }
    };

    private static MeshNode Instance(string partition, string id, string typePath) => new(id, partition)
    {
        NodeType = typePath, Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}\n\ninstance" }
    };

    private static MeshNode TypeNode(string partition, string id) => new(id, partition)
    {
        NodeType = MeshNode.NodeTypePath, Name = id, State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { Configuration = "config => config" }
    };

    private sealed class FakeRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; set; } = [];
        public MeshNode? Root { get; set; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
    }
}
