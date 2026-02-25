using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for the reactive ObserveQuery functionality used by ProjectViews.
/// These tests verify that views update correctly when todos are created, updated, or deleted.
/// </summary>
public class ProjectViewsReactiveTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly DataChangeNotifier _changeNotifier = new();
    private InMemoryPersistenceService? _persistence;
    private IMeshQueryProvider? _meshQuery;
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    private InMemoryPersistenceService Persistence => _persistence ??= new InMemoryPersistenceService(changeNotifier: _changeNotifier);
    private IMeshQueryProvider MeshQuery => _meshQuery ??= new InMemoryMeshQuery(Persistence, changeNotifier: _changeNotifier);

    [Fact]
    public async Task ObserveQuery_EmitsAddedOnNewTodo()
    {
        // Arrange - Create initial todo
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "path:Demos/ACME/Project/Todo nodeType:Demos/ACME/Project/Todo state:Active scope:subtree"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Create new todo
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task2") with
        {
            Name = "Task 2",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task2", Title = "Task 2", Status = "Pending" }
        }, JsonOptions);

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
        // Arrange - Create initial todo as Active
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "path:Demos/ACME/Project/Todo nodeType:Demos/ACME/Project/Todo state:Active scope:subtree"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Soft delete by changing state to Deleted
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }, JsonOptions);

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
        // Arrange - Create initial todo
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1 - Pending",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }, JsonOptions);

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "path:Demos/ACME/Project/Todo nodeType:Demos/ACME/Project/Todo state:Active scope:subtree"), JsonOptions)
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial
        receivedChanges.Should().HaveCount(1);

        // Act - Update the todo status
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1 - Completed",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Completed" }
        }, JsonOptions);

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
        // Arrange - Create initial todo
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }, JsonOptions);

        var deletedChanges = new List<QueryResultChange<MeshNode>>();
        var deletedSubscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "path:Demos/ACME/Project/Todo nodeType:Demos/ACME/Project/Todo state:Deleted scope:subtree"), JsonOptions)
            .Subscribe(change => deletedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial - no deleted items
        deletedChanges.Should().HaveCount(1);
        deletedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        deletedChanges[0].Items.Should().BeEmpty();

        // Act - Soft delete the todo
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }, JsonOptions);

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
        // Arrange - Create a deleted todo
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }, JsonOptions);

        var activeChanges = new List<QueryResultChange<MeshNode>>();
        var deletedChanges = new List<QueryResultChange<MeshNode>>();

        var activeSubscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "path:Demos/ACME/Project/Todo nodeType:Demos/ACME/Project/Todo state:Active scope:subtree"), JsonOptions)
            .Subscribe(change => activeChanges.Add(change));

        var deletedSubscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "path:Demos/ACME/Project/Todo nodeType:Demos/ACME/Project/Todo state:Deleted scope:subtree"), JsonOptions)
            .Subscribe(change => deletedChanges.Add(change));

        await Task.Delay(200);

        // Assert initial states
        activeChanges.Should().HaveCount(1);
        activeChanges[0].Items.Should().BeEmpty();
        deletedChanges.Should().HaveCount(1);
        deletedChanges[0].Items.Should().HaveCount(1);

        // Act - Restore the todo (change state to Active)
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Task 1",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }, JsonOptions);

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
        // This test simulates the AllTasks view which combines active and deleted streams

        // Arrange - Create one active and one deleted todo
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task1") with
        {
            Name = "Active Task",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Active Task", Status = "Pending" }
        }, JsonOptions);

        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task2") with
        {
            Name = "Deleted Task",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task2", Title = "Deleted Task", Status = "Completed" }
        }, JsonOptions);

        var combinedResults = new List<(List<MeshNode> Active, List<MeshNode> Deleted)>();

        var activeStream = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "path:Demos/ACME/Project/Todo nodeType:Demos/ACME/Project/Todo state:Active scope:subtree"), JsonOptions)
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

        var deletedStream = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "path:Demos/ACME/Project/Todo nodeType:Demos/ACME/Project/Todo state:Deleted scope:subtree"), JsonOptions)
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Demos/ACME/Project/Todo/task3") with
        {
            Name = "New Active Task",
            NodeType = "Demos/ACME/Project/Todo",
            State = MeshNodeState.Active,
            Content = new { Id = "task3", Title = "New Active Task", Status = "InProgress" }
        }, JsonOptions);

        await Task.Delay(300);

        // Assert - Combined result updated
        lastResult = combinedResults.Last();
        lastResult.Active.Should().HaveCount(2);
        lastResult.Deleted.Should().HaveCount(1);

        subscription.Dispose();
    }
}
