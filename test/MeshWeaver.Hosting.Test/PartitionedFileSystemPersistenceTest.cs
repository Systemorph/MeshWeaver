using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Tests for partitioned file system persistence.
/// Verifies that routing by first path segment works correctly,
/// auto-provisioning creates partitions on demand, and queries
/// fan out to all partitions when no path is specified.
/// </summary>
public class PartitionedFileSystemPersistenceTest : IDisposable
{
    private readonly string _testDirectory;
    private readonly DataChangeNotifier _changeNotifier;
    private readonly FileSystemPartitionedStoreFactory _factory;
    private readonly RoutingPersistenceServiceCore _router;
    private readonly RoutingMeshQueryProvider _queryProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    public PartitionedFileSystemPersistenceTest()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverPartitionTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        _changeNotifier = new DataChangeNotifier();
        _factory = new FileSystemPartitionedStoreFactory(_testDirectory, null, _changeNotifier);
        _router = new RoutingPersistenceServiceCore(_factory);
        _queryProvider = new RoutingMeshQueryProvider(_router);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public void Dispose()
    {
        _changeNotifier.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    #region Save and Route

    [Fact]
    public async Task SaveNode_DifferentPartitions_RoutesCorrectly()
    {
        // Arrange & Act
        var acmeNode = MeshNode.FromPath("ACME") with { Name = "ACME Corp", NodeType = "Organization" };
        var contosoNode = MeshNode.FromPath("Contoso") with { Name = "Contoso Ltd", NodeType = "Organization" };

        await _router.SaveNodeAsync(acmeNode, _jsonOptions);
        await _router.SaveNodeAsync(contosoNode, _jsonOptions);

        // Assert - Both nodes are accessible
        var acmeResult = await _router.GetNodeAsync("ACME", _jsonOptions);
        var contosoResult = await _router.GetNodeAsync("Contoso", _jsonOptions);

        acmeResult.Should().NotBeNull();
        acmeResult!.Name.Should().Be("ACME Corp");
        contosoResult.Should().NotBeNull();
        contosoResult!.Name.Should().Be("Contoso Ltd");
    }

    [Fact]
    public async Task SaveNode_NestedPath_RoutesToCorrectPartition()
    {
        // Arrange & Act
        var node = MeshNode.FromPath("ACME/Article") with
        {
            Name = "Insurance Article",
            NodeType = "Article"
        };
        await _router.SaveNodeAsync(node, _jsonOptions);

        // Assert
        var result = await _router.GetNodeAsync("ACME/Article", _jsonOptions);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Insurance Article");

        // File should exist on disk
        var filePath = Path.Combine(_testDirectory, "ACME", "Article.json");
        File.Exists(filePath).Should().BeTrue("file should be created at the correct path");
    }

    [Fact]
    public async Task SaveNode_EmptyPath_ThrowsArgumentException()
    {
        var node = new MeshNode("", null) { Name = "Invalid" };
        var act = () => _router.SaveNodeAsync(node, _jsonOptions);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region GetChildren

    [Fact]
    public async Task GetChildren_RootLevel_ReturnsFromAllPartitions()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso") with { Name = "Contoso" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Fabrikam") with { Name = "Fabrikam" }, _jsonOptions);

        // Act - root-level children
        var children = await _router.GetChildrenAsync(null, _jsonOptions).ToListAsync();

        // Assert
        children.Should().HaveCount(3);
        children.Select(c => c.Name).Should().Contain(new[] { "ACME", "Contoso", "Fabrikam" });
    }

    [Fact]
    public async Task GetChildren_WithPath_RoutesToSinglePartition()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/SubDiv") with { Name = "SubDiv" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/Banking") with { Name = "Banking" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso/Marketing") with { Name = "Marketing" }, _jsonOptions);

        // Act - children of ACME only
        var children = await _router.GetChildrenAsync("ACME", _jsonOptions).ToListAsync();

        // Assert
        children.Should().HaveCount(2);
        children.Select(c => c.Name).Should().Contain(new[] { "SubDiv", "Banking" });
        children.Select(c => c.Name).Should().NotContain("Marketing");
    }

    [Fact]
    public async Task GetChildren_EmptyString_ReturnsFromAllPartitions()
    {
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso") with { Name = "Contoso" }, _jsonOptions);

        var children = await _router.GetChildrenAsync("", _jsonOptions).ToListAsync();
        children.Should().HaveCount(2);
    }

    #endregion

    #region GetDescendants

    [Fact]
    public async Task GetDescendants_RootLevel_FansOutToAllPartitions()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/SubDiv") with { Name = "SubDiv" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso") with { Name = "Contoso" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso/Marketing") with { Name = "Marketing" }, _jsonOptions);

        // Act
        var descendants = await _router.GetDescendantsAsync(null, _jsonOptions).ToListAsync();

        // Assert - should include root nodes + their descendants
        descendants.Should().HaveCountGreaterThanOrEqualTo(4);
        descendants.Select(d => d.Name).Should().Contain(new[] { "ACME", "SubDiv", "Contoso", "Marketing" });
    }

    [Fact]
    public async Task GetDescendants_WithPath_RoutesToPartition()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/SubDiv") with { Name = "SubDiv" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/Article") with { Name = "Article" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso/Marketing") with { Name = "Marketing" }, _jsonOptions);

        // Act - descendants of ACME only
        var descendants = await _router.GetDescendantsAsync("ACME", _jsonOptions).ToListAsync();

        // Assert
        descendants.Select(d => d.Name).Should().Contain(new[] { "SubDiv", "Article" });
        descendants.Select(d => d.Name).Should().NotContain("Marketing");
    }

    #endregion

    #region Search

    [Fact]
    public async Task Search_NoPath_SearchesAllPartitions()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/SubDiv") with { Name = "SubDiv One" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso/SubDiv") with { Name = "SubDiv Two" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/Banking") with { Name = "Banking Division" }, _jsonOptions);

        // Act - search all partitions
        var results = await _router.SearchAsync(null, "SubDiv", _jsonOptions).ToListAsync();

        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Name).Should().Contain(new[] { "SubDiv One", "SubDiv Two" });
    }

    [Fact]
    public async Task Search_WithPath_RoutesToPartition()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/SubDiv") with { Name = "SubDiv One" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso/SubDiv") with { Name = "SubDiv Two" }, _jsonOptions);

        // Act - search ACME partition only
        var results = await _router.SearchAsync("ACME", "SubDiv", _jsonOptions).ToListAsync();

        // Assert
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("SubDiv One");
    }

    #endregion

    #region Auto-Provisioning

    [Fact]
    public async Task AutoProvision_NewSegmentOnSave_CreatesPartition()
    {
        // Act - Save to a new partition
        await _router.SaveNodeAsync(MeshNode.FromPath("NewOrg") with { Name = "New Organization" }, _jsonOptions);

        // Assert - Partition directory should be created
        var partitionDir = Path.Combine(_testDirectory, "NewOrg");
        Directory.Exists(partitionDir).Should().BeTrue("partition directory should be auto-created");

        // Node should be retrievable
        var node = await _router.GetNodeAsync("NewOrg", _jsonOptions);
        node.Should().NotBeNull();
        node!.Name.Should().Be("New Organization");
    }

    [Fact]
    public async Task Initialize_DiscoversExistingPartitions()
    {
        // Arrange - Pre-create some partition directories with root node files
        // Root node "Alpha" is stored at baseDir/Alpha.json (path "Alpha" = id="Alpha", ns="")
        Directory.CreateDirectory(Path.Combine(_testDirectory, "Alpha"));
        await File.WriteAllTextAsync(
            Path.Combine(_testDirectory, "Alpha.json"),
            """{"id":"Alpha","name":"Alpha Corp","nodeType":"Organization"}""");

        Directory.CreateDirectory(Path.Combine(_testDirectory, "Beta"));
        await File.WriteAllTextAsync(
            Path.Combine(_testDirectory, "Beta.json"),
            """{"id":"Beta","name":"Beta Inc","nodeType":"Organization"}""");

        // Act - Create a new router to discover existing partitions
        var freshFactory = new FileSystemPartitionedStoreFactory(_testDirectory, null, _changeNotifier);
        var freshRouter = new RoutingPersistenceServiceCore(freshFactory);
        await freshRouter.InitializeAsync();

        // Assert - Partitions should be discovered
        var children = await freshRouter.GetChildrenAsync(null, _jsonOptions).ToListAsync();
        children.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_InOnePartition_DoesNotAffectOther()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/ToDelete") with { Name = "To Delete" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso/ToKeep") with { Name = "To Keep" }, _jsonOptions);

        // Act
        await _router.DeleteNodeAsync("ACME/ToDelete");

        // Assert
        var deleted = await _router.GetNodeAsync("ACME/ToDelete", _jsonOptions);
        deleted.Should().BeNull("deleted node should not be found");

        var kept = await _router.GetNodeAsync("Contoso/ToKeep", _jsonOptions);
        kept.Should().NotBeNull("node in other partition should be unaffected");
        kept!.Name.Should().Be("To Keep");
    }

    [Fact]
    public async Task Delete_NonexistentPartition_DoesNotThrow()
    {
        // Act & Assert - Should not throw
        await _router.DeleteNodeAsync("NonExistent/SomePath");
    }

    #endregion

    #region Move

    [Fact]
    public async Task Move_WithinSamePartition_Works()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/OldName") with
        {
            Name = "Original Name",
            NodeType = "Department"
        }, _jsonOptions);

        // Act
        var moved = await _router.MoveNodeAsync("ACME/OldName", "ACME/NewName", _jsonOptions);

        // Assert
        moved.Path.Should().Be("ACME/NewName");
        moved.Name.Should().Be("Original Name");

        var oldNode = await _router.GetNodeAsync("ACME/OldName", _jsonOptions);
        oldNode.Should().BeNull("old path should not exist after move");

        var newNode = await _router.GetNodeAsync("ACME/NewName", _jsonOptions);
        newNode.Should().NotBeNull();
    }

    [Fact]
    public async Task Move_AcrossPartitions_CopiesAndDeletes()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/Department") with
        {
            Name = "Sales Department",
            NodeType = "Department"
        }, _jsonOptions);

        // Act - Move from ACME to Contoso
        var moved = await _router.MoveNodeAsync("ACME/Department", "Contoso/Department", _jsonOptions);

        // Assert
        moved.Path.Should().Be("Contoso/Department");
        moved.Name.Should().Be("Sales Department");

        var oldNode = await _router.GetNodeAsync("ACME/Department", _jsonOptions);
        oldNode.Should().BeNull("node should not exist in source partition after cross-partition move");

        var newNode = await _router.GetNodeAsync("Contoso/Department", _jsonOptions);
        newNode.Should().NotBeNull();
        newNode!.Name.Should().Be("Sales Department");
    }

    #endregion

    #region Exists

    [Fact]
    public async Task Exists_ReturnsTrue_WhenNodeExistsInPartition()
    {
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/Test") with { Name = "Test" }, _jsonOptions);

        var exists = await _router.ExistsAsync("ACME/Test");
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task Exists_ReturnsFalse_WhenPartitionNotProvisioned()
    {
        var exists = await _router.ExistsAsync("Unknown/SomePath");
        exists.Should().BeFalse();
    }

    #endregion

    #region Query Provider Routing

    [Fact]
    public async Task Query_NoNamespace_FansOutToAll()
    {
        // Arrange - Save nodes in different partitions
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Organization" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/SubDiv") with { Name = "SubDiv", NodeType = "Division" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso") with { Name = "Contoso", NodeType = "Organization" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso/Sales") with { Name = "Sales", NodeType = "Division" }, _jsonOptions);

        // Act - Query for all Organizations (no path constraint)
        var request = MeshQueryRequest.FromQuery("nodeType:Organization scope:descendants");
        var results = await _queryProvider.QueryAsync(request, _jsonOptions).ToListAsync();

        // Assert - Should find organizations from both partitions
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        results.OfType<MeshNode>().Select(n => n.Name).Should().Contain(new[] { "ACME", "Contoso" });
    }

    [Fact]
    public async Task Query_WithNamespace_RoutesToPartition()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Organization" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME/SubDiv") with { Name = "SubDiv", NodeType = "Division" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso") with { Name = "Contoso", NodeType = "Organization" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso/Sales") with { Name = "Sales", NodeType = "Division" }, _jsonOptions);

        // Act - Query within ACME namespace only
        var request = MeshQueryRequest.FromQuery("nodeType:Division path:ACME scope:descendants");
        var results = await _queryProvider.QueryAsync(request, _jsonOptions).ToListAsync();

        // Assert - Should only find ACME's divisions
        results.OfType<MeshNode>().Select(n => n.Name).Should().Contain("SubDiv");
        results.OfType<MeshNode>().Select(n => n.Name).Should().NotContain("Sales");
    }

    #endregion

    #region Partition Storage

    [Fact]
    public async Task PartitionObjects_RoutedByNodePath()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "Insurance" }, _jsonOptions);

        var objects = new List<object> { new { Id = "obj1", Type = "LayoutArea" } };

        // Act
        await _router.SavePartitionObjectsAsync("ACME", "layoutAreas", objects, _jsonOptions);

        // Assert
        var retrieved = await _router.GetPartitionObjectsAsync("ACME", "layoutAreas", _jsonOptions).ToListAsync();
        retrieved.Should().HaveCount(1);
    }

    #endregion

    #region Secure Operations

    [Fact]
    public async Task GetChildrenSecure_RootLevel_FansOutToAllPartitions()
    {
        // Arrange
        await _router.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME" }, _jsonOptions);
        await _router.SaveNodeAsync(MeshNode.FromPath("Contoso") with { Name = "Contoso" }, _jsonOptions);

        // Act - secure root-level children
        var children = await _router.GetChildrenSecureAsync(null, "user1", _jsonOptions).ToListAsync();

        // Assert - should return root nodes from all partitions
        children.Should().HaveCount(2);
    }

    #endregion

    #region PathPartition Utility

    [Theory]
    [InlineData("ACME/Article", "ACME")]
    [InlineData("ACME", "ACME")]
    [InlineData("ACME", "ACME")]
    [InlineData("/ACME/", "ACME")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void GetFirstSegment_ExtractsCorrectly(string? path, string? expected)
    {
        PathPartition.GetFirstSegment(path).Should().Be(expected);
    }

    #endregion
}
