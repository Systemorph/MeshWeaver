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
/// Tests for the reactive ObserveQuery functionality used by ProjectViews.
/// These tests verify that views update correctly when todos are created, updated, or deleted.
/// Each test uses a unique base path to avoid "Node already exists" conflicts.
/// </summary>
public class ProjectViewsReactiveTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshQuery Query => MeshQuery;

    [Fact]
    public async Task ObserveQuery_EmitsAddedOnNewTodo()
    {
        var basePath = $"ACME/Group/Markdown/Added_{Guid.NewGuid():N}";

        // Arrange - Create initial todo
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Create new todo
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task2") with
        {
            Name = "Task 2",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task2", Title = "Task 2", Status = "Pending" }
        });

        await Task.Delay(300);

        // Assert - Added notification
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Task 2");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_EmitsRemovedOnSoftDelete()
    {
        var basePath = $"ACME/Group/Markdown/Removed_{Guid.NewGuid():N}";

        // Arrange - Create initial todo as Active
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Soft delete by changing state to Deleted
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });

        await Task.Delay(300);

        // Assert - Removed notification (no longer matches state:Active filter)
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Removed);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Task 1");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_EmitsUpdatedOnStatusChange()
    {
        var basePath = $"ACME/Group/Markdown/Updated_{Guid.NewGuid():N}";

        // Arrange - Create initial todo
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1 - Pending",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial
        receivedChanges.Should().HaveCount(1);

        // Act - Update the todo status
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1 - Completed",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Completed" }
        });

        await Task.Delay(300);

        // Assert - Updated notification
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Task 1 - Completed");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_DeletedItemsAppearInDeletedQuery()
    {
        var basePath = $"ACME/Group/Markdown/Deleted_{Guid.NewGuid():N}";

        // Arrange - Create initial todo
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });

        var deletedChanges = new List<QueryResultChange<MeshNode>>();
        var deletedSubscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Deleted scope:subtree"))
            .Subscribe(change => deletedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial - no deleted items
        deletedChanges.Should().HaveCount(1);
        deletedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        deletedChanges[0].Items.Should().BeEmpty();

        // Act - Soft delete the todo
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });

        await Task.Delay(300);

        // Assert - Added notification in deleted query
        deletedChanges.Should().HaveCount(2);
        deletedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        deletedChanges[1].Items.Should().HaveCount(1);
        deletedChanges[1].Items[0].Name.Should().Be("Task 1");

        deletedSubscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_RestoreMovesFromDeletedToActive()
    {
        var basePath = $"ACME/Group/Markdown/Restore_{Guid.NewGuid():N}";

        // Arrange - Create a todo then soft-delete it
        // (CreateNodeAsync always confirms to Active, so we must update to Deleted after)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });

        var activeChanges = new List<QueryResultChange<MeshNode>>();
        var deletedChanges = new List<QueryResultChange<MeshNode>>();

        var activeSubscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Subscribe(change => activeChanges.Add(change));

        var deletedSubscription = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Deleted scope:subtree"))
            .Subscribe(change => deletedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial states
        activeChanges.Should().HaveCount(1);
        activeChanges[0].Items.Should().BeEmpty();
        deletedChanges.Should().HaveCount(1);
        deletedChanges[0].Items.Should().HaveCount(1);

        // Act - Restore the todo (change state to Active)
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        });

        await Task.Delay(300);

        // Assert - Active query gets Added, Deleted query gets Removed
        activeChanges.Should().HaveCount(2);
        activeChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        activeChanges[1].Items.Should().HaveCount(1);

        deletedChanges.Should().HaveCount(2);
        deletedChanges[1].ChangeType.Should().Be(QueryChangeType.Removed);
        deletedChanges[1].Items.Should().HaveCount(1);

        activeSubscription.Dispose();
        deletedSubscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_CombineLatestUpdatesOnEitherChange()
    {
        var basePath = $"ACME/Group/Markdown/Combined_{Guid.NewGuid():N}";

        // This test simulates the AllTasks view which combines active and deleted streams

        // Arrange - Create one active and one deleted todo
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Active Task",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Active Task", Status = "Pending" }
        });

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task2") with
        {
            Name = "Deleted Task",
            NodeType = "Markdown",
            Content = new { Id = "task2", Title = "Deleted Task", Status = "Completed" }
        });
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath($"{basePath}/task2") with
        {
            Name = "Deleted Task",
            NodeType = "Markdown",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task2", Title = "Deleted Task", Status = "Completed" }
        });

        var combinedResults = new List<(List<MeshNode> Active, List<MeshNode> Deleted)>();

        var activeStream = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Scan(new List<MeshNode>(), (current, change) =>
            {
                var result = change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset
                    ? change.Items.ToList()
                    : new List<MeshNode>(current);

                foreach (var item in change.Items)
                {
                    if (change.ChangeType == QueryChangeType.Removed)
                        result.RemoveAll(n => n.Path == item.Path);
                    else if (change.ChangeType == QueryChangeType.Added || change.ChangeType == QueryChangeType.Updated)
                    {
                        result.RemoveAll(n => n.Path == item.Path);
                        result.Add(item);
                    }
                }
                return result;
            });

        var deletedStream = Query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Deleted scope:subtree"))
            .Scan(new List<MeshNode>(), (current, change) =>
            {
                var result = change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset
                    ? change.Items.ToList()
                    : new List<MeshNode>(current);

                foreach (var item in change.Items)
                {
                    if (change.ChangeType == QueryChangeType.Removed)
                        result.RemoveAll(n => n.Path == item.Path);
                    else if (change.ChangeType == QueryChangeType.Added || change.ChangeType == QueryChangeType.Updated)
                    {
                        result.RemoveAll(n => n.Path == item.Path);
                        result.Add(item);
                    }
                }
                return result;
            });

        var subscription = activeStream
            .CombineLatest(deletedStream, (active, deleted) => (active, deleted))
            .Subscribe(result => combinedResults.Add(result));

        await Task.Delay(300);

        // Assert initial combined state
        combinedResults.Should().NotBeEmpty();
        var lastResult = combinedResults.Last();
        lastResult.Active.Should().HaveCount(1);
        lastResult.Deleted.Should().HaveCount(1);

        // Act - Add another active task
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{basePath}/task3") with
        {
            Name = "New Active Task",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task3", Title = "New Active Task", Status = "InProgress" }
        });

        await Task.Delay(300);

        // Assert - Combined result updated
        lastResult = combinedResults.Last();
        lastResult.Active.Should().HaveCount(2);
        lastResult.Deleted.Should().HaveCount(1);

        subscription.Dispose();
    }
}
