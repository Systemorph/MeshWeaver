using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Tests for the reactive Query functionality used by ProjectViews.
/// These tests verify that views update correctly when todos are created, updated, or deleted.
/// Each test uses a unique base path to avoid "Node already exists" conflicts.
/// </summary>
public class ProjectViewsReactiveTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private IMeshService Query => MeshQuery;

    /// <summary>
    /// Block (polling the accumulator size on a 50 ms interval) until
    /// <paramref name="changes"/> has at least <paramref name="expectedMinCount"/>
    /// items, or the timeout elapses (a failed assertion). Synchronous sibling
    /// of the old await-based helper — keeps the test bodies <c>void</c> and
    /// await-free per the reactive assertion model.
    /// </summary>
    private static void WaitForChanges<T>(
        List<T> changes,
        int expectedMinCount,
        int timeoutMs = 30_000)
        => Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => changes.Count >= expectedMinCount)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs)).Emit(
                $"expected at least {expectedMinCount} change(s) on the accumulator");

    [Fact]
    public void ObserveQuery_EmitsAddedOnNewTodo()
    {
        var basePath = $"ACME/Group/Markdown/Added_{Guid.NewGuid():N}";

        // Arrange - Create initial todo
        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);

        // Assert initial
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Create new todo
        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task2") with
        {
            Name = "Task 2",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task2", Title = "Task 2", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        WaitForChanges(receivedChanges, 2);

        // Assert - Added notification
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Task 2");

        subscription.Dispose();
    }

    [Fact]
    public void ObserveQuery_EmitsRemovedOnSoftDelete()
    {
        var basePath = $"ACME/Group/Markdown/Removed_{Guid.NewGuid():N}";

        // Arrange - Create initial todo as Active
        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);

        // Assert initial
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(1);

        // Act - Soft delete by changing state to Deleted
        NodeFactory.UpdateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        WaitForChanges(receivedChanges, 2);

        // Assert - Removed notification (no longer matches state:Active filter)
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Removed);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Task 1");

        subscription.Dispose();
    }

    [Fact]
    public void ObserveQuery_EmitsUpdatedOnStatusChange()
    {
        var basePath = $"ACME/Group/Markdown/Updated_{Guid.NewGuid():N}";

        // Arrange - Create initial todo
        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1 - Pending",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);

        // Assert initial
        receivedChanges.Should().HaveCount(1);

        // Act - Update the todo status
        NodeFactory.UpdateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1 - Completed",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Completed" }
        }).Should().Within(30.Seconds()).Emit();

        WaitForChanges(receivedChanges, 2);

        // Assert - Updated notification
        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Task 1 - Completed");

        subscription.Dispose();
    }

    [Fact]
    public void ObserveQuery_DeletedItemsAppearInDeletedQuery()
    {
        var basePath = $"ACME/Group/Markdown/Deleted_{Guid.NewGuid():N}";

        // Arrange - Create initial todo
        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        var deletedChanges = new List<QueryResultChange<MeshNode>>();
        var deletedSubscription = Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Deleted scope:subtree"))
            .Subscribe(change => deletedChanges.Add(change));

        WaitForChanges(deletedChanges, 1);

        // Assert initial - no deleted items
        deletedChanges.Should().HaveCount(1);
        deletedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        deletedChanges[0].Items.Should().BeEmpty();

        // Act - Soft delete the todo
        NodeFactory.UpdateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        WaitForChanges(deletedChanges, 2);

        // Assert - Added notification in deleted query
        deletedChanges.Should().HaveCount(2);
        deletedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        deletedChanges[1].Items.Should().HaveCount(1);
        deletedChanges[1].Items[0].Name.Should().Be("Task 1");

        deletedSubscription.Dispose();
    }

    [Fact]
    public void ObserveQuery_RestoreMovesFromDeletedToActive()
    {
        var basePath = $"ACME/Group/Markdown/Restore_{Guid.NewGuid():N}";

        // Arrange - Create a todo then soft-delete it
        // (CreateNode always confirms to Active, so we must update to Deleted after)
        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();
        NodeFactory.UpdateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        var activeChanges = new List<QueryResultChange<MeshNode>>();
        var deletedChanges = new List<QueryResultChange<MeshNode>>();

        var activeSubscription = Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Active scope:subtree"))
            .Subscribe(change => activeChanges.Add(change));

        var deletedSubscription = Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{basePath} nodeType:Markdown state:Deleted scope:subtree"))
            .Subscribe(change => deletedChanges.Add(change));

        // Wait for BOTH initial emissions (one per query subscription).
        WaitForChanges(activeChanges, 1);
        WaitForChanges(deletedChanges, 1);

        // Assert initial states
        activeChanges.Should().HaveCount(1);
        activeChanges[0].Items.Should().BeEmpty();
        deletedChanges.Should().HaveCount(1);
        deletedChanges[0].Items.Should().HaveCount(1);

        // Act - Restore the todo (change state to Active)
        NodeFactory.UpdateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Task 1",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Task 1", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        // Wait for the restore to propagate to BOTH streams.
        WaitForChanges(activeChanges, 2);
        WaitForChanges(deletedChanges, 2);

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
    public void ObserveQuery_CombineLatestUpdatesOnEitherChange()
    {
        var basePath = $"ACME/Group/Markdown/Combined_{Guid.NewGuid():N}";

        // This test simulates the AllTasks view which combines active and deleted streams

        // Arrange - Create one active and one deleted todo
        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task1") with
        {
            Name = "Active Task",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task1", Title = "Active Task", Status = "Pending" }
        }).Should().Within(30.Seconds()).Emit();

        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task2") with
        {
            Name = "Deleted Task",
            NodeType = "Markdown",
            Content = new { Id = "task2", Title = "Deleted Task", Status = "Completed" }
        }).Should().Within(30.Seconds()).Emit();
        NodeFactory.UpdateNode(MeshNode.FromPath($"{basePath}/task2") with
        {
            Name = "Deleted Task",
            NodeType = "Markdown",
            State = MeshNodeState.Deleted,
            Content = new { Id = "task2", Title = "Deleted Task", Status = "Completed" }
        }).Should().Within(30.Seconds()).Emit();

        var combinedResults = new List<(List<MeshNode> Active, List<MeshNode> Deleted)>();

        var activeStream = Query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
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
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
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

        // Wait until the combined accumulator reflects the seeded state
        // (1 active + 1 deleted) — replaces a fixed Task.Delay(300).
        // LastOrDefault on a value-tuple list returns default(tuple) whose
        // fields are null, so check Count > 0 before reading members.
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => combinedResults.Count > 0
                && combinedResults[^1].Active?.Count == 1
                && combinedResults[^1].Deleted?.Count == 1)
            .Should().Within(5.Seconds()).Emit();

        // Assert initial combined state
        combinedResults.Should().NotBeEmpty();
        var lastResult = combinedResults.Last();
        lastResult.Active.Should().HaveCount(1);
        lastResult.Deleted.Should().HaveCount(1);

        // Act - Add another active task
        NodeFactory.CreateNode(MeshNode.FromPath($"{basePath}/task3") with
        {
            Name = "New Active Task",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new { Id = "task3", Title = "New Active Task", Status = "InProgress" }
        }).Should().Within(30.Seconds()).Emit();

        // Wait until the combined accumulator reflects the new active task —
        // replaces a fixed Task.Delay(300).
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => combinedResults.Count > 0
                && combinedResults[^1].Active?.Count == 2
                && combinedResults[^1].Deleted?.Count == 1)
            .Should().Within(5.Seconds()).Emit();

        // Assert - Combined result updated
        lastResult = combinedResults.Last();
        lastResult.Active.Should().HaveCount(2);
        lastResult.Deleted.Should().HaveCount(1);

        subscription.Dispose();
    }
}
