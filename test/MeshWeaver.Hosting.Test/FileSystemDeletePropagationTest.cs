using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Tests that verify file system deletions propagate correctly through the persistence layer.
/// These tests expose the issue where external file deletions don't update the
/// in-memory caches in persistence services.
/// </summary>
[Collection("FileSystemWatcherTests")]
public class FileSystemDeletePropagationTest : IDisposable
{
    private readonly string _testDirectory;
    private readonly DataChangeNotifier _changeNotifier;
    private readonly FileSystemStorageAdapter _storageAdapter;
    private readonly InMemoryPersistenceService _persistence;
    private readonly FileSystemChangeWatcher _watcher;
    private readonly JsonSerializerOptions _jsonOptions;

    public FileSystemDeletePropagationTest()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        _changeNotifier = new DataChangeNotifier();
        _storageAdapter = new FileSystemStorageAdapter(_testDirectory);
        _persistence = new InMemoryPersistenceService(_storageAdapter, _changeNotifier);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        _watcher = new FileSystemChangeWatcher(_testDirectory, _storageAdapter, _changeNotifier, _jsonOptions)
        {
            DebounceIntervalMs = 50
        };
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _changeNotifier.Dispose();

        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Tests that when a file is deleted externally, the InMemoryPersistenceService.GetNodeAsync
    /// no longer returns the deleted node.
    ///
    /// EXPECTED BEHAVIOR: After external file deletion, GetNodeAsync should return null.
    /// CURRENT ISSUE: The in-memory _nodes dictionary is not updated when external deletions occur,
    /// because nothing subscribes to IDataChangeNotifier to invalidate the cache.
    /// </summary>
    [Fact]
    public async Task ExternalFileDeletion_ShouldRemoveNodeFromPersistence()
    {
        // Arrange - Create a node via persistence
        var node = MeshNode.FromPath("test/node1") with
        {
            Name = "Test Node 1",
            NodeType = "TestType"
        };
        await _persistence.SaveNodeAsync(node, _jsonOptions);

        // Verify the file was created
        var filePath = Path.Combine(_testDirectory, "test", "node1.json");
        File.Exists(filePath).Should().BeTrue("file should be created");

        // Verify the node is accessible
        var retrievedNode = await _persistence.GetNodeAsync("test/node1", _jsonOptions);
        retrievedNode.Should().NotBeNull();
        retrievedNode!.Name.Should().Be("Test Node 1");

        // Track change notifications
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        // Start the file system watcher
        _watcher.Start();
        await Task.Delay(100); // Wait for watcher to be ready

        // Act - Delete the file externally (simulating another process/editor)
        File.Delete(filePath);

        // Wait for file system events and processing
        await Task.Delay(500);

        // Assert - Verify delete notification was received
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("test/node1") && n.Kind == DataChangeKind.Deleted,
            "delete notification should be received from FileSystemChangeWatcher");

        // Assert - The node should no longer be accessible via persistence
        // THIS IS THE KEY TEST: Does the delete propagate to the in-memory cache?
        var nodeAfterDelete = await _persistence.GetNodeAsync("test/node1", _jsonOptions);
        nodeAfterDelete.Should().BeNull(
            "node should be null after external file deletion, " +
            "because the delete notification should have invalidated the in-memory cache");

        // Also verify ExistsAsync returns false
        var exists = await _persistence.ExistsAsync("test/node1");
        exists.Should().BeFalse(
            "ExistsAsync should return false after external file deletion");
    }

    /// <summary>
    /// Tests that GetChildren no longer includes a deleted node.
    /// </summary>
    [Fact]
    public async Task ExternalFileDeletion_ShouldPropagateToGetChildren()
    {
        // Arrange - Create multiple nodes
        await _persistence.SaveNodeAsync(MeshNode.FromPath("parent/child1") with { Name = "Child 1" }, _jsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("parent/child2") with { Name = "Child 2" }, _jsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("parent/child3") with { Name = "Child 3" }, _jsonOptions);

        // Verify all children exist
        var childrenBefore = await _persistence.GetChildrenAsync("parent", _jsonOptions).ToListAsync();
        childrenBefore.Should().HaveCount(3);

        // Track notifications
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        // Start watcher
        _watcher.Start();
        await Task.Delay(100);

        // Act - Delete child2 externally
        var child2Path = Path.Combine(_testDirectory, "parent", "child2.json");
        File.Delete(child2Path);

        // Wait for propagation
        await Task.Delay(500);

        // Assert - GetChildren should only return 2 children
        var childrenAfter = await _persistence.GetChildrenAsync("parent", _jsonOptions).ToListAsync();
        childrenAfter.Should().HaveCount(2,
            "only 2 children should remain after external deletion");
        childrenAfter.Select(c => c.Name).Should().Contain("Child 1");
        childrenAfter.Select(c => c.Name).Should().Contain("Child 3");
        childrenAfter.Select(c => c.Name).Should().NotContain("Child 2",
            "Child 2 was deleted externally");
    }

    /// <summary>
    /// Tests that FileSystemPersistenceService properly invalidates its cache
    /// when external file deletions occur.
    /// </summary>
    [Fact]
    public async Task FileSystemPersistenceService_ExternalDeletion_ShouldInvalidateCache()
    {
        // Arrange - Create a FileSystemPersistenceService (which uses its own MemoryCache)
        var fileSystemPersistence = new FileSystemPersistenceService(_storageAdapter, _changeNotifier);

        // Create a node
        var node = MeshNode.FromPath("fscache/node1") with
        {
            Name = "FS Cache Test Node",
            NodeType = "TestType"
        };
        await fileSystemPersistence.SaveNodeAsync(node, _jsonOptions);

        // Verify the file was created and is accessible (which also warms the cache)
        var filePath = Path.Combine(_testDirectory, "fscache", "node1.json");
        File.Exists(filePath).Should().BeTrue();

        var cachedNode = await fileSystemPersistence.GetNodeAsync("fscache/node1", _jsonOptions);
        cachedNode.Should().NotBeNull();
        cachedNode!.Name.Should().Be("FS Cache Test Node");

        // Track notifications
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        // Start watcher
        _watcher.Start();
        await Task.Delay(100);

        // Act - Delete the file externally
        File.Delete(filePath);

        // Wait for propagation
        await Task.Delay(500);

        // Assert - Verify delete notification was received
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("fscache/node1") && n.Kind == DataChangeKind.Deleted);

        // Assert - The node should no longer be accessible via FileSystemPersistenceService
        // If the cache is not invalidated, this will still return the cached node
        var nodeAfterDelete = await fileSystemPersistence.GetNodeAsync("fscache/node1", _jsonOptions);
        nodeAfterDelete.Should().BeNull(
            "FileSystemPersistenceService should return null after external file deletion, " +
            "because the MemoryCache should have been invalidated by the delete notification");
    }

    /// <summary>
    /// Verifies that the FileSystemChangeWatcher correctly emits Deleted notifications
    /// when files are removed.
    /// </summary>
    [Fact]
    public async Task FileSystemChangeWatcher_EmitsDeleteNotification_WhenFileRemoved()
    {
        // Arrange - Create a file directly
        var dir = Path.Combine(_testDirectory, "watcher-test");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "node1.json");
        await File.WriteAllTextAsync(filePath, """
            {
                "id": "node1",
                "name": "Watcher Test Node"
            }
            """);

        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        // Start watcher
        _watcher.Start();
        await Task.Delay(100);

        // Act - Delete the file
        File.Delete(filePath);

        // Wait for detection
        await Task.Delay(500);

        // Assert - A Deleted notification should have been emitted
        receivedNotifications.Should().Contain(n =>
            n.Path.Contains("watcher-test/node1") && n.Kind == DataChangeKind.Deleted,
            "FileSystemChangeWatcher should emit a Deleted notification when a file is removed");
    }

    /// <summary>
    /// Tests that when a directory containing nodes is deleted, all nodes are removed.
    /// </summary>
    [Fact]
    public async Task ExternalDirectoryDeletion_ShouldPropagateForAllNodes()
    {
        // Arrange - Create a hierarchy
        await _persistence.SaveNodeAsync(MeshNode.FromPath("group/item1") with { Name = "Item 1" }, _jsonOptions);
        await _persistence.SaveNodeAsync(MeshNode.FromPath("group/item2") with { Name = "Item 2" }, _jsonOptions);

        // Verify nodes exist
        var item1 = await _persistence.GetNodeAsync("group/item1", _jsonOptions);
        var item2 = await _persistence.GetNodeAsync("group/item2", _jsonOptions);
        item1.Should().NotBeNull();
        item2.Should().NotBeNull();

        // Track notifications
        var receivedNotifications = new List<DataChangeNotification>();
        _changeNotifier.Subscribe(n => receivedNotifications.Add(n));

        // Start watcher
        _watcher.Start();
        await Task.Delay(100);

        // Act - Delete the entire group directory
        var groupDir = Path.Combine(_testDirectory, "group");
        Directory.Delete(groupDir, recursive: true);

        // Wait for propagation (may need longer for directory deletion)
        await Task.Delay(1000);

        // Assert - All nodes should be gone
        var item1AfterDelete = await _persistence.GetNodeAsync("group/item1", _jsonOptions);
        var item2AfterDelete = await _persistence.GetNodeAsync("group/item2", _jsonOptions);

        item1AfterDelete.Should().BeNull("item1 should be null after directory deletion");
        item2AfterDelete.Should().BeNull("item2 should be null after directory deletion");
    }

    /// <summary>
    /// Baseline test: verifies that DeleteNodeAsync called through the proper API
    /// correctly propagates the deletion.
    /// </summary>
    [Fact]
    public async Task DeleteNodeAsync_ShouldCorrectlyDeleteNode()
    {
        // Arrange - Create a node
        var node = MeshNode.FromPath("delete-test/node1") with
        {
            Name = "Node to Delete",
            NodeType = "TestType"
        };
        await _persistence.SaveNodeAsync(node, _jsonOptions);

        // Verify it exists
        var nodeBeforeDelete = await _persistence.GetNodeAsync("delete-test/node1", _jsonOptions);
        nodeBeforeDelete.Should().NotBeNull();

        // Act - Delete via API
        await _persistence.DeleteNodeAsync("delete-test/node1");

        // Assert - Node should be gone
        var nodeAfterDelete = await _persistence.GetNodeAsync("delete-test/node1", _jsonOptions);
        nodeAfterDelete.Should().BeNull("node should be deleted via DeleteNodeAsync API");

        // File should also be gone
        var filePath = Path.Combine(_testDirectory, "delete-test", "node1.json");
        File.Exists(filePath).Should().BeFalse("file should be deleted");
    }
}

/// <summary>
/// Test collection to ensure FileSystemWatcher tests don't run in parallel
/// </summary>
[CollectionDefinition("FileSystemWatcherTests", DisableParallelization = true)]
public class FileSystemWatcherTestCollection { }
