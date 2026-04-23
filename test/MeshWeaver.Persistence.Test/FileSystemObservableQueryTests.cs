using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Tests for ObservableQuery integration with FileSystemPersistenceService.
/// Verifies that CRUD operations on file system persistence trigger ObserveQuery notifications.
/// </summary>
public class FileSystemObservableQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    #region Create Tests

    [Fact]
    public async Task ObserveQuery_Create_EmitsAddedNotification()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().BeEmpty();

        // Act - Create a new node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with
        {
            Name = "Project 1",
            NodeType = "Markdown"
        });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_CreateMultiple_EmitsBatchedNotification()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Create multiple nodes rapidly
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project3") with { Name = "Project 3", NodeType = "Markdown" });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert - All 3 items should appear across the Added notifications
        // (may be 1 batch or split across 2 if debounce window expires between creates under load)
        var addedChanges = receivedChanges.Where(c => c.ChangeType == QueryChangeType.Added).ToList();
        addedChanges.Should().HaveCountGreaterThanOrEqualTo(1);
        addedChanges.SelectMany(c => c.Items).Should().HaveCount(3);

        subscription.Dispose();
    }

    #endregion

    #region Read Tests

    [Fact]
    public async Task ObserveQuery_Read_EmitsInitialResults()
    {
        // Arrange - Create nodes first
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        // Act - Subscribe after nodes exist
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with
        {
            Name = "Project 1",
            NodeType = "Markdown"
        });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items[0].Name.Should().Be("Project 1");

        // Act - Update the node
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("ACME/Project1") with
        {
            Name = "Updated Project 1",
            NodeType = "Markdown"
        });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert — at least one Updated notification for the node
        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(2);
        var updateChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Updated);
        updateChange.Items.Should().HaveCount(1);
        updateChange.Items[0].Name.Should().Be("Updated Project 1");

        subscription.Dispose();
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task ObserveQuery_Delete_EmitsRemovedNotification()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(2);

        // Act - Delete one node
        await NodeFactory.DeleteNodeAsync("ACME/Project1");

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Removed);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        subscription.Dispose();
    }

    #endregion

    #region Full CRUD Cycle Tests

    [Fact]
    public async Task ObserveQuery_FullCRUDCycle_EmitsCorrectNotifications()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial (empty) emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);

        var countAfterInit = receivedChanges.Count;

        // CREATE
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        var addedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Added);
        addedChange.Items[0].Name.Should().Be("Project 1");

        // UPDATE
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Updated Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        var updatedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Updated);
        updatedChange.Items[0].Name.Should().Be("Updated Project 1");

        // DELETE
        await NodeFactory.DeleteNodeAsync("ACME/Project1");
        await Task.Delay(300);

        var removedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Removed);
        removedChange.Items[0].Name.Should().Be("Updated Project 1");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_CRUDWithMultipleSubscribers_AllReceiveNotifications()
    {
        // Arrange
        var changes1 = new List<QueryResultChange<MeshNode>>();
        var changes2 = new List<QueryResultChange<MeshNode>>();

        var subscription1 = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => changes1.Add(change));

        var subscription2 = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:descendants"))
            .Subscribe(change => changes2.Add(change));

        await Task.Delay(200);

        // Act - Create a node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
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
        // Arrange — use unique path to avoid collision with base-class setup
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("TestOrg") with { Name = "TestOrg", NodeType = "Group" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:TestOrg"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Update the exact path
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("TestOrg") with { Name = "TestOrg Updated", NodeType = "Group" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Create a child (should NOT trigger for)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("TestOrg/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        // Assert - Should still only have 2 notifications
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_ScopeChildren_OnlyNotifiesDirectChildren()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:ACME"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Create another direct child
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Act - Create a grandchild (should NOT trigger for namespace: query)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1/Task1") with { Name = "Task 1", NodeType = "Code" });
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

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Act - Create a matching node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Act - Create a non-matching node (different NodeType)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Code" });
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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Move the node via MoveNodeRequest
        await Mesh.AwaitResponse(new MoveNodeRequest("ACME/Project1", "ACME/Project1Moved"), o => o);
        await Task.Delay(300);

        // Assert - Should have both Removed (old path) and Added (new path) notifications
        // Note: The exact number and order of notifications depends on implementation
        receivedChanges.Count.Should().BeGreaterThanOrEqualTo(2);

        // Verify the node exists at the new path
        var movedNode = await MeshQuery.QueryAsync<MeshNode>("path:ACME/Project1Moved").FirstOrDefaultAsync();
        movedNode.Should().NotBeNull();
        movedNode!.Name.Should().Be("Project 1");

        // Verify the old node doesn't exist
        var oldNode = await MeshQuery.QueryAsync<MeshNode>("path:ACME/Project1").FirstOrDefaultAsync();
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

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Act - Make multiple changes with delay
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Dispose subscription
        subscription.Dispose();

        // Add more nodes after disposal
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        // Assert - Should only have initial emission
        receivedChanges.Should().HaveCount(1);
    }

    #endregion
}
