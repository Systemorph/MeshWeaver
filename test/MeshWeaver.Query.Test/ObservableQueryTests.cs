using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Query.Test;

public class ObservableQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService Query => MeshQuery;

    [Fact]
    public async Task ObserveQuery_EmitsInitialResults()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        // Act
        var subscription = Query
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

    [Fact]
    public async Task ObserveQuery_EmitsAddedOnNewNode()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Add a new matching node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 2");

        subscription.Dispose();
    }

[Fact]
    public async Task ObserveQuery_EmitsRemovedOnDeletedNode()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Delete a node
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

    [Fact]
    public async Task ObserveQuery_IgnoresChangesOutsideScope()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Add a node outside the scope (different path)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Other/Project2") with { Name = "Project 2", NodeType = "Markdown" });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert - Should still only have initial emission
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_IgnoresChangesNotMatchingFilter()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Add a node within scope but not matching filter
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Code" });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert - Should still only have initial emission (the new node doesn't match nodeType:Markdown)
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_BatchesRapidChanges()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial empty emission
        await Task.Delay(200);

        // Act - Add multiple nodes rapidly (within debounce window)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project3") with { Name = "Project 3", NodeType = "Markdown" });

        // Wait for debounce and processing
        await Task.Delay(300);

        // Assert - Changes should be batched into one Added emission
        // Should have: 1 initial (empty) + 1 added (with 3 items)
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(3);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_VersionIncrementsWithEachChange()
    {
        // Arrange
        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Add nodes one at a time with delay
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

    [Fact]
    public async Task ObserveQuery_DisposalStopsNotifications()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);

        // Act - Dispose subscription
        subscription.Dispose();

        // Add more nodes after disposal
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        // Assert - Should only have initial emission (no changes after disposal)
        receivedChanges.Should().HaveCount(1);
    }

    [Fact]
    public async Task ObserveQuery_ScopeExact_OnlyNotifiesOnExactPath()
    {
        // Arrange — use a unique path to avoid collision with base-class setup
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("TestOrg") with { Name = "TestOrg", NodeType = "Group" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:TestOrg"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Modify the exact path
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("TestOrg") with { Name = "TestOrg Updated", NodeType = "Group" });
        await Task.Delay(300);

        // Should get updated notification
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add a child (should NOT trigger notification for)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("TestOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await Task.Delay(300);

        // Should still only have 2 notifications
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_ScopeChildren_OnlyNotifiesOnDirectChildren()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:ACME"))
            .Subscribe(change => receivedChanges.Add(change));

        // Wait for initial emission
        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        // Act - Add a direct child
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Act - Add a grandchild (should NOT trigger notification for namespace: query)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1/Task") with { Name = "Task", NodeType = "Code" });
        await Task.Delay(300);

        // Should still only have 2 notifications
        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }
}
