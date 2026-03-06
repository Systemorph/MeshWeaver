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

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for observable queries with InMemory persistence.
/// Tests end-to-end scenarios including multiple concurrent subscriptions.
/// </summary>
public class ObservableQueryIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshQuery Query => MeshQuery;

    [Fact]
    public async Task MultipleConcurrentSubscriptions_EachReceivesCorrectChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Task" });

        var projectChanges = new List<QueryResultChange<MeshNode>>();
        var taskChanges = new List<QueryResultChange<MeshNode>>();

        // Subscribe to two different queries concurrently
        var projectSubscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => projectChanges.Add(change));

        var taskSubscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Task scope:descendants"))
            .Subscribe(change => taskChanges.Add(change));

        await Task.Delay(200);

        // Initial emissions
        projectChanges.Should().HaveCount(1);
        projectChanges[0].Items.Should().HaveCount(1);

        taskChanges.Should().HaveCount(1);
        taskChanges[0].Items.Should().HaveCount(1);

        // Act - Add a new project (should only affect project subscription)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Task.Delay(300);

        projectChanges.Should().HaveCount(2);
        projectChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        taskChanges.Should().HaveCount(1); // No change for task query

        // Act - Add a new task (should only affect task subscription)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Task2") with { Name = "Task 2", NodeType = "Task" });
        await Task.Delay(300);

        projectChanges.Should().HaveCount(2); // No change for project query
        taskChanges.Should().HaveCount(2);
        taskChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        projectSubscription.Dispose();
        taskSubscription.Dispose();
    }

    [Fact]
    public async Task ScopeExact_OnlyNotifiesOnExactPathChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Organization" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:exact"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial

        // Act - Update exact path
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME Updated", NodeType = "Organization" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add child (should NOT trigger)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Project" });
        await Task.Delay(300);

        changes.Should().HaveCount(2); // No change

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeChildren_OnlyNotifiesOnDirectChildChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:children"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial

        // Act - Add direct child
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Added);

        // Act - Add grandchild (should NOT trigger for children scope)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1/Task") with { Name = "Task", NodeType = "Task" });
        await Task.Delay(300);

        changes.Should().HaveCount(2); // No change

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeDescendants_NotifiesOnAllDescendantChanges()
    {
        // Arrange
        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:descendants"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial (empty)

        // Act - Add child
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Project" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);

        // Act - Add grandchild
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/Task") with { Name = "Task", NodeType = "Task" });
        await Task.Delay(300);

        changes.Should().HaveCount(3);

        // Act - Add great-grandchild
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/Task/Subtask") with { Name = "Subtask", NodeType = "Task" });
        await Task.Delay(300);

        changes.Should().HaveCount(4);

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeAncestors_NotifiesOnAncestorChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Organization" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Project" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/Task") with { Name = "Task", NodeType = "Task" });

        var changes = new List<QueryResultChange<MeshNode>>();

        // Subscribe from the deepest path looking at ancestors
        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME/Project/Task scope:ancestors"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial with ACME and ACME/Project

        // Act - Update an ancestor
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME Updated", NodeType = "Organization" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add a sibling of Task (should NOT trigger for ancestors scope)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/Task2") with { Name = "Task 2", NodeType = "Task" });
        await Task.Delay(300);

        changes.Should().HaveCount(2); // No change

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeSubtree_NotifiesOnSelfAndDescendantChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Organization" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:subtree"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial with ACME

        // Act - Update self
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME Updated", NodeType = "Organization" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);

        // Act - Add descendant
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Project" });
        await Task.Delay(300);

        changes.Should().HaveCount(3);

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeHierarchy_NotifiesOnAncestorsSelfAndDescendantChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Root") with { Name = "Root", NodeType = "Organization" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Root/ACME") with { Name = "ACME", NodeType = "Company" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Root/ACME/Project") with { Name = "Project", NodeType = "Project" });

        var changes = new List<QueryResultChange<MeshNode>>();

        // Subscribe from the middle of the hierarchy
        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:Root/ACME scope:hierarchy"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial with Root, ACME, and Project

        // Act - Update ancestor
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Root") with { Name = "Root Updated", NodeType = "Organization" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);

        // Act - Update self
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Root/ACME") with { Name = "ACME Updated", NodeType = "Company" });
        await Task.Delay(300);

        changes.Should().HaveCount(3);

        // Act - Update descendant
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Root/ACME/Project") with { Name = "Project Updated", NodeType = "Project" });
        await Task.Delay(300);

        changes.Should().HaveCount(4);

        // Act - Add new descendant
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Root/ACME/Project/Task") with { Name = "Task", NodeType = "Task" });
        await Task.Delay(300);

        changes.Should().HaveCount(5);

        subscription.Dispose();
    }

    [Fact]
    public async Task RecursiveDelete_EmitsRemovedForAllDeletedNodes()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Organization" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Project" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/Task") with { Name = "Task", NodeType = "Task" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME scope:subtree"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial with 3 items
        changes[0].Items.Should().HaveCount(3);

        // Act - Recursive delete
        await NodeFactory.DeleteNodeAsync("ACME", recursive: true);
        await Task.Delay(300);

        // Assert - Should have removal for all 3 items
        var allRemovedItems = changes
            .Where(c => c.ChangeType == QueryChangeType.Removed)
            .SelectMany(c => c.Items)
            .ToList();

        allRemovedItems.Should().HaveCount(3);

        subscription.Dispose();
    }

    [Fact]
    public async Task QueryWithFilter_OnlyEmitsMatchingChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Project" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Project scope:descendants"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1);

        // Act - Add matching node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Project" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);

        // Act - Add non-matching node (different nodeType)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Task" });
        await Task.Delay(300);

        // Should still be 2 (Task doesn't match nodeType:Project filter)
        changes.Should().HaveCount(2);

        subscription.Dispose();
    }
}
