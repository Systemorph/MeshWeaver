using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Integration tests for observable queries with InMemory persistence.
/// Tests end-to-end scenarios including multiple concurrent subscriptions.
/// </summary>
public class ObservableQueryIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    private IMeshService Query => MeshQuery;

    [Fact]
    public async Task MultipleConcurrentSubscriptions_EachReceivesCorrectChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Code" });

        var projectChanges = new List<QueryResultChange<MeshNode>>();
        var taskChanges = new List<QueryResultChange<MeshNode>>();

        // Subscribe to two different queries concurrently
        var projectSubscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => projectChanges.Add(change));

        var taskSubscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Code scope:descendants"))
            .Subscribe(change => taskChanges.Add(change));

        await Task.Delay(200);

        // Initial emissions
        projectChanges.Should().HaveCount(1);
        projectChanges[0].Items.Should().HaveCount(1);

        taskChanges.Should().HaveCount(1);
        taskChanges[0].Items.Should().HaveCount(1);

        // Act - Add a new project (should only affect project subscription)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        projectChanges.Should().HaveCount(2);
        projectChanges[1].ChangeType.Should().Be(QueryChangeType.Added);

        taskChanges.Should().HaveCount(1); // No change for task query

        // Act - Add a new task (should only affect task subscription)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Task2") with { Name = "Task 2", NodeType = "Code" });
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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ExactOrg") with { Name = "ExactOrg", NodeType = "Group" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ExactOrg"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial

        // Act - Update exact path
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("ExactOrg") with { Name = "ExactOrg Updated", NodeType = "Group" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add child (should NOT trigger)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ExactOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await Task.Delay(300);

        changes.Should().HaveCount(2); // No change

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeChildren_OnlyNotifiesOnDirectChildChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:ACME"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial

        // Act - Add direct child
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Added);

        // Act - Add grandchild (should NOT trigger for children scope)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1/Task") with { Name = "Task", NodeType = "Code" });
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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Markdown" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);

        // Act - Add grandchild
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/Task") with { Name = "Task", NodeType = "Code" });
        await Task.Delay(300);

        changes.Should().HaveCount(3);

        // Act - Add great-grandchild
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project/Task/Subtask") with { Name = "Subtask", NodeType = "Code" });
        await Task.Delay(300);

        changes.Should().HaveCount(4);

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeAncestors_NotifiesOnAncestorChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("AncOrg") with { Name = "AncOrg", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("AncOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("AncOrg/Project/Task") with { Name = "Task", NodeType = "Code" });

        var changes = new List<QueryResultChange<MeshNode>>();

        // Subscribe from the deepest path looking at ancestors
        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:AncOrg/Project/Task scope:ancestors"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial with AncOrg and AncOrg/Project

        // Act - Update an ancestor
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("AncOrg") with { Name = "AncOrg Updated", NodeType = "Group" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);
        changes[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Act - Add a sibling of Task (should NOT trigger for ancestors scope)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("AncOrg/Project/Task2") with { Name = "Task 2", NodeType = "Code" });
        await Task.Delay(300);

        changes.Should().HaveCount(2); // No change

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeSubtree_NotifiesOnSelfAndDescendantChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("SubOrg") with { Name = "SubOrg", NodeType = "Group" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:SubOrg scope:subtree"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial with SubOrg

        // Act - Update self
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("SubOrg") with { Name = "SubOrg Updated", NodeType = "Group" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);

        // Act - Add descendant
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("SubOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await Task.Delay(300);

        changes.Should().HaveCount(3);

        subscription.Dispose();
    }

    [Fact]
    public async Task ScopeHierarchy_NotifiesOnAncestorsSelfAndDescendantChanges()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("HRoot") with { Name = "HRoot", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("HRoot/HCo") with { Name = "HCo", NodeType = "Code" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("HRoot/HCo/Project") with { Name = "Project", NodeType = "Markdown" });

        var changes = new List<QueryResultChange<MeshNode>>();

        // Subscribe from the middle of the hierarchy
        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:HRoot/HCo scope:hierarchy"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial with HRoot, HCo, and Project

        // Act - Update ancestor
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("HRoot") with { Name = "HRoot Updated", NodeType = "Group" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);

        // Act - Update self
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("HRoot/HCo") with { Name = "HCo Updated", NodeType = "Code" });
        await Task.Delay(300);

        changes.Should().HaveCount(3);

        // Act - Update descendant
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath("HRoot/HCo/Project") with { Name = "Project Updated", NodeType = "Markdown" });
        await Task.Delay(300);

        changes.Should().HaveCount(4);

        // Act - Add new descendant
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("HRoot/HCo/Project/Task") with { Name = "Task", NodeType = "Code" });
        await Task.Delay(300);

        changes.Should().HaveCount(5);

        subscription.Dispose();
    }

    [Fact]
    public async Task RecursiveDelete_EmitsRemovedForAllDeletedNodes()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("DelOrg") with { Name = "DelOrg", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("DelOrg/Project") with { Name = "Project", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("DelOrg/Project/Task") with { Name = "Task", NodeType = "Code" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:DelOrg scope:subtree"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1); // Initial with 3 items
        changes[0].Items.Should().HaveCount(3);

        // Act - Recursive delete
        await NodeFactory.DeleteNodeAsync("DelOrg");
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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project1") with { Name = "Project 1", NodeType = "Markdown" });

        var changes = new List<QueryResultChange<MeshNode>>();

        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("path:ACME nodeType:Markdown scope:descendants"))
            .Subscribe(change => changes.Add(change));

        await Task.Delay(200);
        changes.Should().HaveCount(1);

        // Act - Add matching node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Project2") with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        changes.Should().HaveCount(2);

        // Act - Add non-matching node (different nodeType)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("ACME/Task1") with { Name = "Task 1", NodeType = "Code" });
        await Task.Delay(300);

        // Should still be 2 (Task doesn't match nodeType:Markdown filter)
        changes.Should().HaveCount(2);

        subscription.Dispose();
    }
}
