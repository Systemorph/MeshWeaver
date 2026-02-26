using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for ObservableQuery integration with FileSystemPersistenceService.
/// Verifies that CRUD operations on file system persistence trigger ObserveQuery notifications.
/// </summary>
public class FileSystemObservableQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected IPersistenceService Persistence => Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
    protected IMeshQuery MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

    #region Create Tests

    [Fact]
    public async Task ObserveQuery_Create_EmitsAddedNotification()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().BeEmpty();

        // Act - Create a new node
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with
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

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_CreateMultiple_EmitsBatchedNotification()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Create multiple nodes rapidly
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project3") with { Name = "Project 3", NodeType = "Project" });

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project2") with { Name = "Project 2", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        // Act - Subscribe after nodes exist
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with
        {
            Name = "Project 1",
            NodeType = "Project"
        });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items[0].Name.Should().Be("Project 1");

        // Act - Update the node
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with
        {
            Name = "Updated Project 1",
            NodeType = "Project"
        });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Updated Project 1");

        subscription.Dispose();
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task ObserveQuery_Delete_EmitsRemovedNotification()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project2") with { Name = "Project 2", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(2);

        // Act - Delete one node
        await Persistence.DeleteNodeAsync("ACME/Software/Project1");

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
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial (empty) emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().BeEmpty();

        // CREATE
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        // UPDATE
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Updated Project 1", NodeType = "Project" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(3);
        receivedChanges[2].ChangeType.Should().Be(QueryChangeType.Updated);
        receivedChanges[2].Items[0].Name.Should().Be("Updated Project 1");

        // DELETE
        await Persistence.DeleteNodeAsync("ACME/Software/Project1");
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

        var subscription1 = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => changes1.Add(change));

        var subscription2 = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software scope:descendants"))
            .Subscribe(change => changes2.Add(change));

        await Task.Delay(200);

        // Act - Create a node
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software") with { Name = "ACME", NodeType = "Organization" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software scope:exact"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Update the exact path
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software") with { Name = "ACME Updated", NodeType = "Organization" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Create a child (should NOT trigger for scope:exact)
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        // Assert - Should still only have 2 notifications
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_ScopeChildren_OnlyNotifiesDirectChildren()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software scope:children"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Create another direct child
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Act - Create a grandchild (should NOT trigger for scope:children)
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1/Task1") with { Name = "Task 1", NodeType = "Task" });
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
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Act - Create a matching node
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Act - Create a non-matching node (different NodeType)
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Task1") with { Name = "Task 1", NodeType = "Task" });
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Move the node
        await Persistence.MoveNodeAsync("ACME/Software/Project1", "ACME/Software/Project1Moved");
        await Task.Delay(300);

        // Assert - Should have both Removed (old path) and Added (new path) notifications
        // Note: The exact number and order of notifications depends on implementation
        receivedChanges.Count.Should().BeGreaterThanOrEqualTo(2);

        // Verify the node exists at the new path
        var movedNode = await Persistence.GetNodeAsync("ACME/Software/Project1Moved");
        movedNode.Should().NotBeNull();
        movedNode!.Name.Should().Be("Project 1");

        // Verify the old node doesn't exist
        var oldNode = await Persistence.GetNodeAsync("ACME/Software/Project1");
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
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Act - Make multiple changes with delay
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });
        await Task.Delay(300);

        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project2") with { Name = "Project 2", NodeType = "Project" });
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project1") with { Name = "Project 1", NodeType = "Project" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Software nodeType:Project scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Dispose subscription
        subscription.Dispose();

        // Add more nodes after disposal
        await Persistence.SaveNodeAsync(MeshNode.FromPath("ACME/Software/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Task.Delay(300);

        // Assert - Should only have initial emission
        receivedChanges.Should().HaveCount(1);
    }

    #endregion
}
