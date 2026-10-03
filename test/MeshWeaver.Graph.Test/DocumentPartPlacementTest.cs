using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins WHERE a document's parts and the nodes attached to them are stored
/// (<c>Doc/Architecture/DocumentParts</c>): their own satellite tables in the owning partition's
/// schema, never <c>mesh_nodes</c> and never the table of a satellite the document hangs under —
/// with the MainNode (access delegation) still at the document's owner.
/// </summary>
public class DocumentPartPlacementTest
{
    private static readonly PartitionDefinition Partition = new()
    {
        Namespace = "Admin",
        TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
        NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
    };

    [Fact]
    public void PartPath_IsTheZeroPaddedIndexUnderThePartSegment()
    {
        var doc = DocumentPaths.For("Admin/Jobs/build-42/logs", "run.log");

        DocumentPartPaths.PartPath(doc, 7).Should().Be($"{doc}/_DocumentPart/000007");
        DocumentPartPaths.TryParsePartIndex("000007", out var index).Should().BeTrue();
        index.Should().Be(7);
        DocumentPartPaths.TryParsePartIndex("_PartAnnotation", out _).Should().BeFalse();
    }

    [Theory]
    // A document under a main node.
    [InlineData("Admin/Jobs/build-42/logs")]
    // A document under an activity (a job run) — _DocumentPart must outrank _Activity.
    [InlineData("Admin/_Activity/build-42/logs")]
    // A transcript under a thread — _DocumentPart must outrank _Thread.
    [InlineData("rbuergi/_Thread/t-1/transcripts")]
    public void Parts_ResolveToTheirOwnTable_AndKeepTheOwnersMainNode(string collection)
    {
        var doc = DocumentPaths.For(collection, "run.log");
        var part = DocumentPartPaths.PartPath(doc, 0);

        DocumentPartPaths.PartPlacementProblem(doc).Should().BeNull();
        Partition.ResolveTable(part).Should().Be(DocumentPartPaths.PartTable);
        Partition.ResolveTableByNodeType(DocumentPartPaths.PartNodeType).Should().Be(DocumentPartPaths.PartTable);
        SatelliteTableMapping.IsSatellitePath(part).Should().BeTrue();
        // Access delegates to the document's owner: the FIRST satellite segment decides.
        SatelliteTableMapping.OwnerOfSatellitePath(part)
            .Should().Be(SatelliteTableMapping.OwnerOfSatellitePath(doc));
    }

    [Fact]
    public void Annotations_ResolveToTheirOwnTable_AndDelegateToTheDocumentsOwner()
    {
        var doc = DocumentPaths.For("Admin/Jobs/build-42/logs", "run.log");
        var annotation = $"{DocumentPartPaths.AnnotationNamespace(DocumentPartPaths.PartPath(doc, 3))}/a-1";

        Partition.ResolveTable(annotation).Should().Be(DocumentPartPaths.AnnotationTable);
        Partition.ResolveTableByNodeType(DocumentPartPaths.AnnotationNodeType)
            .Should().Be(DocumentPartPaths.AnnotationTable);
        SatelliteTableMapping.OwnerOfSatellitePath(annotation).Should().Be(doc);
    }

    [Theory]
    // Under a main node the document is a main node.
    [InlineData("Admin/Jobs/build-42/logs", "mesh_nodes")]
    // Under a satellite it lives WITH that satellite — written and read by the same path resolution,
    // so it is found — while its parts still go to their own table.
    [InlineData("rbuergi/_Thread/t-1/transcripts", "threads")]
    [InlineData("Admin/_Activity/build-42/logs", "activities")]
    public void TheLogicalDocument_LivesWhereItsPathResolves(string collection, string table)
    {
        var doc = DocumentPaths.For(collection, "run.log");
        Partition.ResolveTable(doc).Should().Be(table);
        Partition.ResolveTable(DocumentPartPaths.PartPath(doc, 0)).Should().Be(DocumentPartPaths.PartTable);
    }

    [Fact]
    public void PartIds_AreSixDigitsOnly_SoTheySortInPartOrder()
    {
        DocumentPartPaths.PartId(DocumentPartPaths.MaxPartIndex).Should().Be("999999");
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentPartPaths.PartId(DocumentPartPaths.MaxPartIndex + 1));
        DocumentPartPaths.TryParsePartIndex("42", out _).Should().BeFalse("only the canonical six-digit id is a part id");
        DocumentPartPaths.TryParsePartIndex("0000042", out _).Should().BeFalse();
        DocumentPartPaths.TryParsePartIndex("000042", out var index).Should().BeTrue();
        index.Should().Be(42);
    }

    [Fact]
    public void APlacementGuard_ReadsTheHostsOwnLayout_WhenGiven()
    {
        var doc = DocumentPaths.For("Admin/Jobs/build-42/logs", "run.log");
        // A host that maps the part segment elsewhere: the guard must see it, not the defaults.
        var remapped = SatelliteTableMapping.Defaults
            .Select(m => m.Segment == DocumentPartPaths.PartSegment ? m with { Table = "elsewhere" } : m)
            .ToArray();
        DocumentPartPaths.PartPlacementProblem(doc).Should().BeNull();
        DocumentPartPaths.PartPlacementProblem(doc, remapped).Should().Contain("elsewhere");
    }

    [Theory]
    // _ThreadMessage is longer than _DocumentPart: the parts would land in `threads`.
    [InlineData("rbuergi/_Thread/t-1/_ThreadMessage/m-1/logs")]
    // _Notification is as long as _DocumentPart: the tie is refused rather than left to chance.
    [InlineData("rbuergi/_Notification/n-1/logs")]
    public void ADocumentUnderAnOutrankingSatellite_IsRefused(string collection)
    {
        var doc = DocumentPaths.For(collection, "run.log");
        DocumentPartPaths.PartPlacementProblem(doc).Should().NotBeNull();
    }
}
