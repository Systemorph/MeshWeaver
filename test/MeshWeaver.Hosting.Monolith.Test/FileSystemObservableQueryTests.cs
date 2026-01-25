using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for ObservableQuery integration with FileSystemPersistenceService.
/// Verifies that CRUD operations on file system persistence trigger ObserveQuery notifications.
/// </summary>
public class FileSystemObservableQueryTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly DataChangeNotifier _changeNotifier;
    private readonly FileSystemStorageAdapter _storageAdapter;
    private readonly FileSystemPersistenceService _persistence;
    private readonly IMeshQuery _meshQuery;

    public FileSystemObservableQueryTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        _changeNotifier = new DataChangeNotifier();
        _storageAdapter = new FileSystemStorageAdapter(_testDirectory);
        _persistence = new FileSystemPersistenceService(_storageAdapter, _changeNotifier);
        _meshQuery = new InMemoryMeshQuery(_persistence, changeNotifier: _changeNotifier);
    }

    public void Dispose()
    {
        _changeNotifier.Dispose();

        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region Create Tests

    [Fact]
    public async Task ObserveQuery_Create_EmitsAddedNotification()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().BeEmpty();

        // Act - Create a new node
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with
        {
            Name = "Project 1",
            NodeType = "Project"
        });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        // Verify file was created on disk
        var filePath = Path.Combine(_testDirectory, "ACME", "Project1.json");
        File.Exists(filePath).Should().BeTrue();

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_CreateMultiple_EmitsBatchedNotification()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Create multiple nodes rapidly
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project3") with { Name = "Project 3", NodeType = "Project" });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert - Changes should be batched
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(3);

        subscription.Dispose();
    }

    #endregion

    #region Read Tests

    [Fact]
    public async Task ObserveQuery_Read_EmitsInitialResults()
    {
        // Arrange - Create nodes first
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        // Act - Subscribe after nodes exist
        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Assert
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().HaveCount(2);
        receivedChanges[0].Items.Select(n => n.Name).Should().Contain(["Project 1", "Project 2"]);

        subscription.Dispose();
    }

    #endregion

    #region Update Tests

    [Fact]
    public async Task ObserveQuery_Update_EmitsUpdatedNotification()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with
        {
            Name = "Project 1",
            NodeType = "Project"
        });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items[0].Name.Should().Be("Project 1");

        // Act - Update the node
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with
        {
            Name = "Updated Project 1",
            NodeType = "Project",
            Description = "New description"
        });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Updated Project 1");
        receivedChanges[1].Items[0].Description.Should().Be("New description");

        subscription.Dispose();
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task ObserveQuery_Delete_EmitsRemovedNotification()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(2);

        // Act - Delete one node
        await _persistence.DeleteNodeAsync("ACME/Project1");

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Removed);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        // Verify file was deleted from disk
        var filePath = Path.Combine(_testDirectory, "ACME", "Project1.json");
        File.Exists(filePath).Should().BeFalse();

        subscription.Dispose();
    }

    #endregion

    #region Full CRUD Cycle Tests

    [Fact]
    public async Task ObserveQuery_FullCRUDCycle_EmitsCorrectNotifications()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial (empty) emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().BeEmpty();

        // CREATE
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        // UPDATE
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Updated Project 1", NodeType = "Project" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(3);
        receivedChanges[2].ChangeType.Should().Be(QueryChangeType.Updated);
        receivedChanges[2].Items[0].Name.Should().Be("Updated Project 1");

        // DELETE
        await _persistence.DeleteNodeAsync("ACME/Project1");
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(4);
        receivedChanges[3].ChangeType.Should().Be(QueryChangeType.Removed);
        receivedChanges[3].Items[0].Name.Should().Be("Updated Project 1");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_CRUDWithMultipleSubscribers_AllReceiveNotifications()
    {
        // Arrange
        var changes1 = new List<QueryResultChange<MeshNode>>();
        var changes2 = new List<QueryResultChange<MeshNode>>();

        var subscription1 = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => changes1.Add(change));

        var subscription2 = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:descendants"))
            .Subscribe(change => changes2.Add(change));

        await Task.Delay(200);

        // Act - Create a node
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        // Assert - Both subscribers should receive the notification
        changes1.Should().HaveCount(2);
        changes2.Should().HaveCount(2);
        changes1[1].Items[0].Name.Should().Be("Project 1");
        changes2[1].Items[0].Name.Should().Be("Project 1");

        subscription1.Dispose();
        subscription2.Dispose();
    }

    #endregion

    #region Scope Tests

    [Fact]
    public async Task ObserveQuery_ScopeExact_OnlyNotifiesExactPath()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Organization" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:exact"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Update the exact path
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME Updated", NodeType = "Organization" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Create a child (should NOT trigger for scope:exact)
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        // Assert - Should still only have 2 notifications
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_ScopeChildren_OnlyNotifiesDirectChildren()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:children"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Create another direct child
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Act - Create a grandchild (should NOT trigger for scope:children)
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1/Task1") with { Name = "Task 1", NodeType = "Task" });
        await Task.Delay(300);

        // Assert - Should still only have 2 notifications
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task ObserveQuery_WithFilter_IgnoresNonMatchingNodes()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Act - Create a matching node
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Act - Create a non-matching node (different NodeType)
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Task" });
        await Task.Delay(300);

        // Assert - Should still only have 2 notifications (non-matching ignored)
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    #endregion

    #region Move Tests

    [Fact]
    public async Task ObserveQuery_MoveNode_EmitsDeleteAndCreate()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Move the node
        await _persistence.MoveNodeAsync("ACME/Project1", "ACME/Project1Moved");
        await Task.Delay(300);

        // Assert - Should have both Removed (old path) and Added (new path) notifications
        // Note: The exact number and order of notifications depends on implementation
        receivedChanges.Count.Should().BeGreaterThanOrEqualTo(2);

        // Verify the node exists at the new path
        var movedNode = await _persistence.GetNodeAsync("ACME/Project1Moved");
        movedNode.Should().NotBeNull();
        movedNode!.Name.Should().Be("Project 1");

        // Verify the old node doesn't exist
        var oldNode = await _persistence.GetNodeAsync("ACME/Project1");
        oldNode.Should().BeNull();

        subscription.Dispose();
    }

    #endregion

    #region Version Tests

    [Fact]
    public async Task ObserveQuery_VersionIncrementsOnEachChange()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Act - Make multiple changes with delay
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Task.Delay(300);

        // Assert - Versions should be incrementing
        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(2);
        for (int i = 1; i < receivedChanges.Count; i++)
        {
            receivedChanges[i].Version.Should().BeGreaterThan(receivedChanges[i - 1].Version);
        }

        subscription.Dispose();
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task ObserveQuery_DisposalStopsNotifications()
    {
        // Arrange
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = _meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Dispose subscription
        subscription.Dispose();

        // Add more nodes after disposal
        await _persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Task.Delay(300);

        // Assert - Should only have initial emission
        receivedChanges.Should().HaveCount(1);
    }

    #endregion
}
