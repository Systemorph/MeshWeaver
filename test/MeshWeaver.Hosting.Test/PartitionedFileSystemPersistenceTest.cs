using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
        foreach (var dir in new[] { _testDirectory, _testDirectory + "_fresh" })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* ignore cleanup errors */ }
            }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    #region Save and Route

    [Fact]
    public async Task SaveNode_DifferentPartitions_RoutesCorrectly()
    {
        // Arrange & Act
        var acmeNode = MeshNode.FromPath("ACME") with { Name = "ACME Corp", NodeType = "Organization" };
        var contosoNode = MeshNode.FromPath("Contoso") with { Name = "Contoso Ltd", NodeType = "Organization" };

        await _router.SaveNode(acmeNode, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        await _router.SaveNode(contosoNode, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Assert - Both nodes are accessible
        var acmeResult = await _router.GetNodeAsync("ACME", _jsonOptions, TestContext.Current.CancellationToken);
        var contosoResult = await _router.GetNodeAsync("Contoso", _jsonOptions, TestContext.Current.CancellationToken);

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
        await _router.SaveNode(node, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Assert
        var result = await _router.GetNodeAsync("ACME/Article", _jsonOptions, TestContext.Current.CancellationToken);
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
        var act = () => _router.SaveNode(node, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region GetChildren

    [Fact]
    public async Task GetChildren_RootLevel_ReturnsFromAllPartitions()
    {
        // Arrange
        await _router.SaveNode(MeshNode.FromPath("ACME") with { Name = "ACME" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        await _router.SaveNode(MeshNode.FromPath("Contoso") with { Name = "Contoso" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        await _router.SaveNode(MeshNode.FromPath("Fabrikam") with { Name = "Fabrikam" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Act - root-level children
        var children = await _router.GetChildrenAsync(null, _jsonOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        children.Should().HaveCount(3);
        children.Select(c => c.Name).Should().Contain(new[] { "ACME", "Contoso", "Fabrikam" });
    }

    // GetChildren_WithPath_RoutesToSinglePartition deleted in the persistence-cull
    // (2026-05-11): _router.GetChildrenAsync (per-partition variant) is gone. Use
    // `workspace.GetQuery(id, "namespace:ACME scope:children")` — covered by
    // SyncedQueryTest's partition-fan-out cases.

    [Fact]
    public async Task GetChildren_EmptyString_ReturnsFromAllPartitions()
    {
        await _router.SaveNode(MeshNode.FromPath("ACME") with { Name = "ACME" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        await _router.SaveNode(MeshNode.FromPath("Contoso") with { Name = "Contoso" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var children = await _router.GetChildrenAsync("", _jsonOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2);
    }

    #endregion

    #region GetDescendants

    // GetDescendants_* / Search_* tests removed in the persistence-cull (2026-05-11):
    // they exercised _router.GetDescendantsAsync / SearchAsync directly, which are
    // gone. Equivalent partition-fan-out coverage now lives in
    // test/MeshWeaver.Query.Test/SyncedQueryTest.cs via the
    // `workspace.GetQuery(id, query)` pattern — the supported user-facing API.

    #endregion

    #region Auto-Provisioning

    [Fact]
    public async Task AutoProvision_NewSegmentOnSave_CreatesPartition()
    {
        // Act - Save to a new partition
        await _router.SaveNode(MeshNode.FromPath("NewOrg") with { Name = "New Organization" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Assert - Partition directory should be created
        var partitionDir = Path.Combine(_testDirectory, "NewOrg");
        Directory.Exists(partitionDir).Should().BeTrue("partition directory should be auto-created");

        // Node should be retrievable
        var node = await _router.GetNodeAsync("NewOrg", _jsonOptions, TestContext.Current.CancellationToken);
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
            """{"id":"Alpha","name":"Alpha Corp","nodeType":"Organization"}""",
            TestContext.Current.CancellationToken);

        Directory.CreateDirectory(Path.Combine(_testDirectory, "Beta"));
        await File.WriteAllTextAsync(
            Path.Combine(_testDirectory, "Beta.json"),
            """{"id":"Beta","name":"Beta Inc","nodeType":"Organization"}""",
            TestContext.Current.CancellationToken);

        // Act - Create a new router to discover existing partitions
        // Use a unique copy to avoid CachingStorageAdapter's static shared snapshot cache
        var freshDir = _testDirectory + "_fresh";
        CopyDirectory(_testDirectory, freshDir);
        var freshFactory = new FileSystemPartitionedStoreFactory(freshDir, null, _changeNotifier);
        var freshRouter = new RoutingPersistenceServiceCore(freshFactory);
        await freshRouter.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert - Partitions should be discovered
        var children = await freshRouter.GetChildrenAsync(null, _jsonOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_InOnePartition_DoesNotAffectOther()
    {
        // Arrange
        await _router.SaveNode(MeshNode.FromPath("ACME/ToDelete") with { Name = "To Delete" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        await _router.SaveNode(MeshNode.FromPath("Contoso/ToKeep") with { Name = "To Keep" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Act
        await _router.DeleteNode("ACME/ToDelete").FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Assert
        var deleted = await _router.GetNodeAsync("ACME/ToDelete", _jsonOptions, TestContext.Current.CancellationToken);
        deleted.Should().BeNull("deleted node should not be found");

        var kept = await _router.GetNodeAsync("Contoso/ToKeep", _jsonOptions, TestContext.Current.CancellationToken);
        kept.Should().NotBeNull("node in other partition should be unaffected");
        kept!.Name.Should().Be("To Keep");
    }

    [Fact]
    public async Task Delete_NonexistentPartition_DoesNotThrow()
    {
        // Act & Assert - Should not throw
        await _router.DeleteNode("NonExistent/SomePath").FirstAsync().ToTask(TestContext.Current.CancellationToken);
    }

    #endregion

    #region Move

    [Fact]
    public async Task Move_WithinSamePartition_Works()
    {
        // Arrange
        await _router.SaveNode(MeshNode.FromPath("ACME/OldName") with
        {
            Name = "Original Name",
            NodeType = "Department"
        }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Act
        var moved = await _router.MoveNode("ACME/OldName", "ACME/NewName", _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Assert
        moved.Path.Should().Be("ACME/NewName");
        moved.Name.Should().Be("Original Name");

        var oldNode = await _router.GetNodeAsync("ACME/OldName", _jsonOptions, TestContext.Current.CancellationToken);
        oldNode.Should().BeNull("old path should not exist after move");

        var newNode = await _router.GetNodeAsync("ACME/NewName", _jsonOptions, TestContext.Current.CancellationToken);
        newNode.Should().NotBeNull();
    }

    // Move_AcrossPartitions_CopiesAndDeletes deleted in the persistence-cull
    // (2026-05-11): cross-partition recursive move via central descendant load
    // was deleted from the routing layer. The new shape is per-node-hub fan-out
    // (HandleMoveNodeRequest at the source node hub: read self → CreateNodeRequest
    // at target with own content → recurse to children → DeleteNodeRequest at self).
    // Coverage will live alongside the new handler.

    #endregion

    #region Exists

    [Fact]
    public async Task Exists_ReturnsTrue_WhenNodeExistsInPartition()
    {
        await _router.SaveNode(MeshNode.FromPath("ACME/Test") with { Name = "Test" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var exists = await _router.Exists("ACME/Test").FirstAsync().ToTask(TestContext.Current.CancellationToken);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task Exists_ReturnsFalse_WhenPartitionNotProvisioned()
    {
        var exists = await _router.Exists("Unknown/SomePath").FirstAsync().ToTask(TestContext.Current.CancellationToken);
        exists.Should().BeFalse();
    }

    #endregion

    #region Query Provider Routing

    // Query_NoNamespace_FansOutToAll / Query_WithNamespace_RoutesToPartition deleted in
    // the persistence-cull (2026-05-11): the file-system-backed query path went
    // through MeshQueryEngine's naive load-and-match loop, which is gone. The
    // replacement is a FileSystemMeshQueryProvider that scans filenames + (where
    // needed) Regex over file content — backend-aware enumeration. Until that
    // provider exists, file-system query coverage lives at the unit level on the
    // adapter (PathRemappingStorageAdapterTests, etc.). Postgres deployments hit
    // PostgreSqlMeshQuery's SQL pushdown directly; SyncedQueryPgTest covers fan-out.

    #endregion

    #region Partition Storage

    [Fact]
    public async Task PartitionObjects_RoutedByNodePath()
    {
        // Arrange
        await _router.SaveNode(MeshNode.FromPath("ACME") with { Name = "Insurance" }, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var objects = new List<object> { new { Id = "obj1", Type = "LayoutArea" } };

        // Act
        await _router.SavePartitionObjects("ACME", "layoutAreas", objects, _jsonOptions).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Assert
        var retrieved = await _router.GetPartitionObjectsAsync("ACME", "layoutAreas", _jsonOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        retrieved.Should().HaveCount(1);
    }

    #endregion

    #region Secure Operations

    // GetChildrenSecure_RootLevel_FansOutToAllPartitions removed in the
    // persistence-cull (2026-05-11) — _router.GetChildrenSecure deleted along
    // with the rest of the "load all" surface. Permission-filtered listing
    // now flows through `workspace.GetQuery(id, query)` (synced) which pushes
    // RLS into the per-partition provider. Coverage at
    // test/MeshWeaver.Query.Test/SyncedQueryTest.cs.

    #endregion

    #region PathPartition Utility

    [Theory]
    [InlineData("ACME/Article", "ACME")]
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
