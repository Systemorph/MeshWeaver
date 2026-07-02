using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.ContentCollections.Indexing.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// <c>MeshOperations.Get</c> on a node whose PATH contains a UCR keyword as an interior segment.
/// Document nodes live at <c>{collection}/_Documents/{slug}</c> and content collections are
/// conventionally named <c>content</c> — a UCR keyword — so the unified (UCR) interpretation
/// hijacks the path into collection-file resolution. Prod repro (atioz 2026-07-02): <c>get</c> on
/// an existing Document node returned "Error: Content collection '_Documents' not found" instead
/// of the node. The node namespace is authoritative: an existing node at the full path must win.
/// </summary>
public class DocumentNodeGetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The collection segment is deliberately the UCR keyword <c>content</c>.</summary>
    private const string Collection = TestPartition + "/content";

    /// <summary>Registers the Document NodeType + sink on the test mesh.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddDocumentIndexing();

    [Fact(Timeout = 60_000)]
    public async Task Get_DocumentNodePath_ReturnsTheNode_NotTheCollectionError()
    {
        const string filePath = "vertrag_2025.txt";
        var sink = new MeshDocumentSink(Mesh);
        await sink.WriteDocument(new DocumentInfo(
                Collection, filePath, filePath, "Summary of the contract.",
                ContentHash: "abc123", Mime: "text/plain", SizeBytes: 42, ChunkCount: 3))
            .FirstAsync().ToTask();

        var path = DocumentPaths.For(Collection, filePath);

        // The Document write is debounced/persisted — wait until the node is readable before Get.
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .SelectMany(_ => Mesh.GetMeshNode(path, 5.Seconds()))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

        var result = await new MeshOperations(Mesh).Get(path).FirstAsync().ToTask();

        result.Should().NotStartWith("Error:",
            "an existing node at the full path must win over the UCR collection interpretation");
        result.Should().Contain("\"nodeType\":\"Document\"");
        result.Should().Contain(filePath);
    }
}
