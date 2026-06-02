using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
/// Verifies that CRUD operations on file system persistence trigger Query notifications.
///
/// Each [Fact] uses a unique per-run namespace derived from <c>TestPartition</c> to avoid
/// "Node already exists" collisions when the file-system persistence carries state across test
/// class instances (each [Fact] gets its own Mesh but the backing store on disk is shared
/// within a test run). Querying is scoped to the test's own namespace.
/// </summary>
public class FileSystemObservableQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Unique namespace per test class instance so parallel or back-to-back runs
    // within the same process don't collide on the file-system store.
    private readonly string _ns = $"{TestPartition}/FsObsQuery/{Guid.NewGuid():N}";

    private string NodePath(string id) => $"{_ns}/{id}";
    private string QueryFilter(string extra = "") => $"namespace:{_ns} scope:subtree {extra}".TrimEnd();

    /// <summary>
    /// Block (polling the accumulator size on a 50 ms interval) until the
    /// accumulator <paramref name="changes"/> has at least
    /// <paramref name="expectedMinCount"/> items, or the timeout elapses (a
    /// failed assertion). Keeps the test bodies <c>void</c> and await-free per
    /// the reactive assertion model. The 30 s default absorbs CI contention; the
    /// per-method xUnit <c>methodTimeout</c> (60 s) is the upper bound either way.
    /// </summary>
    private static void WaitForChanges<T>(
        List<T> changes,
        int expectedMinCount,
        int timeoutMs = 30_000)
        => Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => changes.Count >= expectedMinCount)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs)).Emit(
                $"expected at least {expectedMinCount} change(s) on the accumulator");

    #region Create Tests

    [Fact]
    public void ObserveQuery_Create_EmitsAddedNotification()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().BeEmpty();

        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with
        {
            Name = "Project 1",
            NodeType = "Markdown"
        }).Should().Within(30.Seconds()).Emit();

        WaitForChanges(receivedChanges, 2);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        subscription.Dispose();
    }

    [Fact]
    public void ObserveQuery_CreateMultiple_EmitsBatchedNotification()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1); // Initial

        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project3")) with { Name = "Project 3", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        // Wait until 3 Added items have been observed (possibly batched into
        // a single Added emission). Polling the inner item-count via
        // Observable.Interval — replaces a fixed Task.Delay(300).
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => receivedChanges
                .Where(c => c.ChangeType == QueryChangeType.Added)
                .SelectMany(c => c.Items)
                .Count() >= 3)
            .Should().Within(5.Seconds()).Emit();

        var addedChanges = receivedChanges.Where(c => c.ChangeType == QueryChangeType.Added).ToList();
        addedChanges.Should().HaveCountGreaterThanOrEqualTo(1);
        addedChanges.SelectMany(c => c.Items).Should().HaveCount(3);

        subscription.Dispose();
    }

    #endregion

    #region Read Tests

    [Fact]
    public void ObserveQuery_Read_EmitsInitialResults()
    {
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);

        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().HaveCount(2);
        receivedChanges[0].Items.Select(n => n.Name).Should().Contain(["Project 1", "Project 2"]);

        subscription.Dispose();
    }

    #endregion

    #region Update Tests

    [Fact]
    public void ObserveQuery_Update_EmitsUpdatedNotification()
    {
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with
        {
            Name = "Project 1",
            NodeType = "Markdown"
        }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items[0].Name.Should().Be("Project 1");

        NodeFactory.UpdateNode(MeshNode.FromPath(NodePath("Project1")) with
        {
            Name = "Updated Project 1",
            NodeType = "Markdown"
        }).Should().Within(30.Seconds()).Emit();

        WaitForChanges(receivedChanges, 2);

        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(2);
        var updateChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Updated);
        updateChange.Items.Should().HaveCount(1);
        updateChange.Items[0].Name.Should().Be("Updated Project 1");

        subscription.Dispose();
    }

    #endregion

    #region Delete Tests

    [Fact]
    public void ObserveQuery_Delete_EmitsRemovedNotification()
    {
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(2);

        NodeFactory.DeleteNode(NodePath("Project1")).Should().Within(30.Seconds()).Emit();

        WaitForChanges(receivedChanges, 2);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Removed);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        subscription.Dispose();
    }

    #endregion

    #region Full CRUD Cycle Tests

    [Fact]
    public void ObserveQuery_FullCRUDCycle_EmitsCorrectNotifications()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);
        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);

        // CREATE
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        WaitForChanges(receivedChanges, 2);

        var addedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Added);
        addedChange.Items[0].Name.Should().Be("Project 1");

        // UPDATE
        NodeFactory.UpdateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Updated Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        WaitForChanges(receivedChanges, 3);

        var updatedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Updated);
        updatedChange.Items[0].Name.Should().Be("Updated Project 1");

        // DELETE — wait specifically for a Removed event, not just "≥4 changes".
        // Under CI load the delete propagation can take longer than the 3s
        // WaitForChanges budget, so the silent-timeout path leaves the test
        // with no Removed in receivedChanges and the .Last() below throws.
        NodeFactory.DeleteNode(NodePath("Project1")).Should().Within(30.Seconds()).Emit();
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => receivedChanges.Any(c => c.ChangeType == QueryChangeType.Removed))
            .Should().Within(10.Seconds()).Emit();

        var removedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Removed);
        removedChange.Items[0].Name.Should().Be("Updated Project 1");

        subscription.Dispose();
    }

    [Fact]
    public void ObserveQuery_CRUDWithMultipleSubscribers_AllReceiveNotifications()
    {
        var changes1 = new List<QueryResultChange<MeshNode>>();
        var changes2 = new List<QueryResultChange<MeshNode>>();

        var subscription1 = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => changes1.Add(change));

        var subscription2 = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter()))
            .Subscribe(change => changes2.Add(change));

        WaitForChanges(changes1, 1);
        WaitForChanges(changes2, 1);

        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        WaitForChanges(changes1, 2);
        WaitForChanges(changes2, 2);

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
    public void ObserveQuery_ScopeExact_OnlyNotifiesExactPath()
    {
        var orgPath = NodePath("TestOrg");
        NodeFactory.CreateNode(MeshNode.FromPath(orgPath) with { Name = "TestOrg", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{orgPath}"))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);
        receivedChanges.Should().HaveCount(1);

        NodeFactory.UpdateNode(MeshNode.FromPath(orgPath) with { Name = "TestOrg Updated", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        WaitForChanges(receivedChanges, 2);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Create a child — should NOT trigger for exact-path query
        NodeFactory.CreateNode(MeshNode.FromPath($"{orgPath}/Project1") with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        // Negative assertion ("no new emission") — a third change must NOT land
        // within a small barrier. The polling observable fires only if a 3rd
        // change is accumulated; NotEmit asserts it never does (300 ms barrier,
        // the original budget, consistent with sibling tests).
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => receivedChanges.Count >= 3)
            .Should().NotEmit(within: TimeSpan.FromMilliseconds(300));

        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    [Fact]
    public void ObserveQuery_ScopeChildren_OnlyNotifiesDirectChildren()
    {
        var proj1 = NodePath("Project1");
        NodeFactory.CreateNode(MeshNode.FromPath(proj1) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{_ns}"))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);
        receivedChanges.Should().HaveCount(1);

        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        WaitForChanges(receivedChanges, 2);

        receivedChanges.Should().HaveCount(2);

        // Create a grandchild — should NOT trigger for namespace query
        NodeFactory.CreateNode(MeshNode.FromPath($"{proj1}/Task1") with { Name = "Task 1", NodeType = "Code" }).Should().Within(30.Seconds()).Emit();
        // Negative assertion — a third change must NOT land within a small barrier.
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => receivedChanges.Count >= 3)
            .Should().NotEmit(within: TimeSpan.FromMilliseconds(300));

        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    #endregion

    #region Filter Tests

    [Fact]
    public void ObserveQuery_WithFilter_IgnoresNonMatchingNodes()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);

        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        WaitForChanges(receivedChanges, 2);

        receivedChanges.Should().HaveCount(2);

        // Create a non-matching node (different NodeType)
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Task1")) with { Name = "Task 1", NodeType = "Code" }).Should().Within(30.Seconds()).Emit();
        // Negative assertion — a third change must NOT land within a small barrier.
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => receivedChanges.Count >= 3)
            .Should().NotEmit(within: TimeSpan.FromMilliseconds(300));

        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    #endregion

    #region Move Tests

    [Fact]
    public void ObserveQuery_MoveNode_EmitsDeleteAndCreate()
    {
        var proj1 = NodePath("Project1");
        NodeFactory.CreateNode(MeshNode.FromPath(proj1) with { Name = "Project 1", NodeType = "Markdown" }).Should().Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(1);

        var movedPath = NodePath("Project1Moved");
        Mesh.Observe(new MoveNodeRequest(proj1, movedPath), o => o).Should().Emit();
        WaitForChanges(receivedChanges, 2);

        receivedChanges.Count.Should().BeGreaterThanOrEqualTo(2);

        var movedNode = ReadNode(movedPath).Should().Emit();
        movedNode.Should().NotBeNull();
        movedNode!.Name.Should().Be("Project 1");

        var oldNode = ReadNode(proj1).Should().Emit();
        oldNode.Should().BeNull();

        subscription.Dispose();
    }

    #endregion

    #region Version Tests

    [Fact]
    public void ObserveQuery_VersionIncrementsOnEachChange()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);

        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        WaitForChanges(receivedChanges, 2);

        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        WaitForChanges(receivedChanges, 3);

        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(2);
        for (int i = 1; i < receivedChanges.Count; i++)
            receivedChanges[i].Version.Should().BeGreaterThan(receivedChanges[i - 1].Version);

        subscription.Dispose();
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public void ObserveQuery_DisposalStopsNotifications()
    {
        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        WaitForChanges(receivedChanges, 1);
        receivedChanges.Should().HaveCount(1);

        subscription.Dispose();

        NodeFactory.CreateNode(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        // Negative assertion — disposed subscription should NOT receive any
        // more emissions. Small barrier to surface a regression if Dispose
        // accidentally leaks the subscription.
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => receivedChanges.Count >= 2)
            .Should().NotEmit(within: TimeSpan.FromMilliseconds(300));

        receivedChanges.Should().HaveCount(1);
    }

    #endregion
}
