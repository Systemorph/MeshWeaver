using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 AN IMPORT MUST NOT COST ONE WRITE PER NODE (Systemorph/MeshWeaver.Plugins#1013).
///
/// <para>The static-repo importer used to post one <c>CreateOrUpdateNodeRequest</c> per source node:
/// one cross-hub round-trip AND one single-row upsert each, so materialising a partition of n nodes
/// cost n of both. The storage layer has had the bulk contract all along —
/// <c>IStorageAdapter.WriteMany</c>, which PostgreSQL implements as ONE <c>NpgsqlBatch</c> per
/// (schema, table) window — but nothing on the import path reached it, because a node write has to
/// route through the mesh's canonical verbs rather than straight at the adapter.</para>
///
/// <para>It now does, through <c>CreateNodesRequest</c> — the bulk sibling of the singular create,
/// which runs the identical pipeline (partition bootstrap, every validator including RLS, the
/// type-existence probe, post-creation handlers) and then ONE <c>WriteMany</c>. Plain creates travel
/// in chunks; everything else keeps the per-node verb, because an UPDATE is applied by the node's own
/// hub and satellites and grants carry per-node lifecycle guards.</para>
///
/// <para>What is measured here is <see cref="StaticRepoImportResult.WriteRequests"/> — the number of
/// write requests actually issued, counted at subscribe — because that is the quantity the change is
/// about. Asserting only "every node landed" would stay green through a revert to per-node writes.</para>
/// </summary>
public class StaticRepoImportBulkWriteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The importer's chunk size. Mirrored here rather than exposed: the test states the arithmetic it
    /// expects (⌈n ÷ chunk⌉ requests) instead of recomputing it from the implementation, so a silent
    /// change to the chunk is a red test to think about, not a quietly-passing one.
    /// </summary>
    private const int Chunk = 25;

    /// <summary>Node count that spans more than one chunk without being slow to import.</summary>
    private const int Pages = 40;

    // 🚨 A DERIVED FIELD INITIALIZER RUNS BEFORE THE BASE CONSTRUCTOR, and the base constructor is
    // what calls ConfigureMesh. So the partition name (and the rejection hook below) are already set
    // by the time the mesh is built and can be closed over by services registered there.
    private readonly string _partition = "Bw" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The one path the test validator rejects, or null. Set by the test that needs a create to be
    /// refused from INSIDE a batch — the only realistic way to reach the bulk verb's
    /// validate-all-then-write refusal without inventing a malformed node.
    /// </summary>
    private string? _rejectPath;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(
                new RejectOnePathValidator(() => _rejectPath)));

    /// <summary>
    /// 🚨 THE REGRESSION GUARD. A first import of a whole partition — every node a plain create —
    /// must cost ⌈n ÷ chunk⌉ write requests, not n. Before batching this was exactly n (40), which is
    /// also 40 single-row upserts at the adapter.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task FirstImportOfAWholePartition_CostsOneRequestPerChunk_NotOnePerNode()
    {
        var source = new FakeRepoSource(_partition)
        {
            Root = Space(_partition),
            Nodes = [.. Enumerable.Range(0, Pages).Select(i => Page(_partition, $"P{i:D2}"))],
        };

        var result = await Import(source);

        result.Count.Should().Be(Pages, "every node in the source must land");
        result.Failed.Should().Be(0);
        result.WriteRequests.Should().Be((Pages + Chunk - 1) / Chunk,
            $"{Pages} plain creates must travel as ⌈{Pages}÷{Chunk}⌉ bulk requests — one request per "
            + "chunk, each reaching storage as ONE WriteMany (on Postgres one NpgsqlBatch per "
            + $"(schema, table) window). {result.WriteRequests} requests for {Pages} nodes means the "
            + "per-node write is back and the import is paying a round-trip and a single-row upsert "
            + "for every file again");

        // …and the batching is not bought by losing content: read two of them back through their own
        // per-node hubs, which is what the change feed had to wake.
        (await Body($"{_partition}/P00")).Should().Contain("page");
        (await Body($"{_partition}/P39")).Should().Contain("page");
    }

    /// <summary>
    /// 🚨 PER-FILE ISOLATION SURVIVES BATCHING. <c>CreateNodesRequest</c> is validate-all-then-write:
    /// ONE offender refuses the whole request and can name at most one path. The import's contract is
    /// the opposite — every other node still has to land, and the one that did not has to be NAMED in
    /// the activity log, because <c>Failed &gt; 0</c> is what holds the git baseline (#2229 item C).
    /// So a refused batch is re-run one node at a time, and what the operator sees is unchanged.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ARejectedNodeInABatch_IsAttributedToItself_AndEveryOtherNodeStillLands()
    {
        _rejectPath = $"{_partition}/Bad";
        var source = new FakeRepoSource(_partition)
        {
            Root = Space(_partition),
            Nodes =
            [
                Page(_partition, "Before"),
                Page(_partition, "Bad"),
                Page(_partition, "After"),
            ],
        };

        var result = await Import(source);

        result.Failed.Should().Be(1,
            "exactly ONE node was rejected — a batched write must not turn one validator refusal into "
            + "three failures, nor swallow it into a green import");
        result.Outcome.Should().Be("ImportedWithErrors");
        result.Count.Should().Be(2, "the other two nodes of the refused batch must still land");
        result.WrittenPaths.Should().HaveCount(2);
        result.WrittenPaths.Should().Contain($"{_partition}/Before");
        result.WrittenPaths.Should().Contain($"{_partition}/After");

        // The re-run is what makes the failure attributable: one bulk attempt plus one request per
        // node in the chunk. A batch that simply failed would have cost one request and named nothing.
        result.WriteRequests.Should().Be(1 + source.Nodes.Count,
            "a refused batch is re-run node by node so the failure lands on the file that caused it");

        (await Body($"{_partition}/Before")).Should().Contain("page");
        (await Body($"{_partition}/After")).Should().Contain("page");
    }

    /// <summary>
    /// A satellite path (any segment starting with <c>_</c>) is refused by the bulk verb on purpose —
    /// satellites carry per-node lifecycle guards and MainNode normalization. The importer must
    /// therefore route it to the per-node verb rather than let it refuse the whole batch, and it must
    /// still land.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ASatellitePath_KeepsThePerNodeVerb_AndStillLands()
    {
        var source = new FakeRepoSource(_partition)
        {
            Root = Space(_partition),
            Nodes =
            [
                Page(_partition, "Plain"),
                Page(_partition, "_Notes"),
                Page(_partition, "Other"),
            ],
        };

        var result = await Import(source);

        result.Failed.Should().Be(0,
            "a satellite the bulk verb cannot carry must be routed AROUND the batch, never left in it "
            + "to refuse the whole request");
        result.Count.Should().Be(3);
        result.WriteRequests.Should().Be(2,
            "the two plain creates travel in ONE bulk request; the satellite takes the per-node verb");
        (await Body($"{_partition}/_Notes")).Should().Contain("page");
    }

    /// <summary>
    /// An UPDATE is applied by the node's OWN hub — <c>GetMeshNodeStream(path).Update(...)</c> — never
    /// by the mesh hub writing over it, so it keeps the per-node verb. This pins that: the second
    /// import of a changed source costs one request per changed node and applies the new content.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task AReimportOfChangedNodes_UpdatesThroughThePerNodeVerb()
    {
        var source = new FakeRepoSource(_partition)
        {
            Root = Space(_partition),
            Nodes = [Page(_partition, "A"), Page(_partition, "B")],
        };
        var first = await Import(source);
        first.Count.Should().Be(2);
        first.WriteRequests.Should().Be(1, "two fresh creates are one batch");

        source.Nodes = [.. source.Nodes.Select(n => n with
        {
            Content = new MarkdownContent { Content = $"# {n.Id}\n\nrevised" }
        })];
        var second = await Import(source);

        second.Failed.Should().Be(0);
        second.Count.Should().Be(2);
        second.WriteRequests.Should().Be(2,
            "an existing node is UPDATED by its own per-node hub, so an update costs its own request — "
            + "batching creates must not have quietly moved updates onto the mesh hub's write path");
        (await Body($"{_partition}/A")).Should().Contain("revised");
        (await Body($"{_partition}/B")).Should().Contain("revised");
    }

    private async Task<StaticRepoImportResult> Import(FakeRepoSource source)
    {
        // 🚨 .Await(), never a bare `await source`: Rx's own awaiter resumes the continuation INLINE
        // on the signalling thread, still inside the trampoline, and every later await in the method
        // inherits that scheduler — the .ToTask() defect wearing different clothes.
        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(180.Seconds()).Await();
        Output.WriteLine(
            $"outcome={result.Outcome} count={result.Count} failed={result.Failed} "
            + $"writeRequests={result.WriteRequests} written=[{string.Join(", ", result.WrittenPaths)}]");
        return result;
    }

    private async Task<string> Body(string path)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(60.Seconds()).Await();
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = "Bulk Write", NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Bulk Write\n\nfixture." }
    };

    private static MeshNode Page(string partition, string id) => new(id, partition)
    {
        NodeType = "Markdown", Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}\n\npage" }
    };

    /// <summary>
    /// Rejects the creation of ONE path, chosen per test. The realistic way to make a node inside a
    /// bulk batch refuse: the bulk verb runs the very same <c>INodeValidator</c> pass the singular
    /// create runs, for every node, before anything is written.
    /// </summary>
    private sealed class RejectOnePathValidator(Func<string?> path) : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => Observable.Return(
                string.Equals(context.Node.Path, path(), StringComparison.Ordinal)
                    ? NodeValidationResult.Invalid($"'{context.Node.Path}' is rejected by the test validator")
                    : NodeValidationResult.Valid());
    }

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
