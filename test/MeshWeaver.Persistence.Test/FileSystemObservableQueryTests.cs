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

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Tests for ObservableQuery integration with FileSystemPersistenceService.
/// Verifies that CRUD operations on file system persistence trigger ObserveQuery notifications.
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

    #region Create Tests

    [Fact]
    public async Task ObserveQuery_Create_EmitsAddedNotification()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);
        receivedChanges[0].Items.Should().BeEmpty();

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with
        {
            Name = "Project 1",
            NodeType = "Markdown"
        });

        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Added);
        receivedChanges[1].Items.Should().HaveCount(1);
        receivedChanges[1].Items[0].Name.Should().Be("Project 1");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_CreateMultiple_EmitsBatchedNotification()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project3")) with { Name = "Project 3", NodeType = "Markdown" });

        await Task.Delay(300);

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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with
        {
            Name = "Project 1",
            NodeType = "Markdown"
        });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items[0].Name.Should().Be("Project 1");

        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with
        {
            Name = "Updated Project 1",
            NodeType = "Markdown"
        });

        await Task.Delay(300);

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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(2);

        await NodeFactory.DeleteNodeAsync(NodePath("Project1"));

        await Task.Delay(300);

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
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(1);
        receivedChanges[0].ChangeType.Should().Be(QueryChangeType.Initial);

        // CREATE
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        var addedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Added);
        addedChange.Items[0].Name.Should().Be("Project 1");

        // UPDATE
        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Updated Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        var updatedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Updated);
        updatedChange.Items[0].Name.Should().Be("Updated Project 1");

        // DELETE
        await NodeFactory.DeleteNodeAsync(NodePath("Project1"));
        await Task.Delay(300);

        var removedChange = receivedChanges.Last(c => c.ChangeType == QueryChangeType.Removed);
        removedChange.Items[0].Name.Should().Be("Updated Project 1");

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_CRUDWithMultipleSubscribers_AllReceiveNotifications()
    {
        var changes1 = new List<QueryResultChange<MeshNode>>();
        var changes2 = new List<QueryResultChange<MeshNode>>();

        var subscription1 = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => changes1.Add(change));

        var subscription2 = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter()))
            .Subscribe(change => changes2.Add(change));

        await Task.Delay(200);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

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
        var orgPath = NodePath("TestOrg");
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(orgPath) with { Name = "TestOrg", NodeType = "Group" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{orgPath}"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        await NodeFactory.UpdateNodeAsync(MeshNode.FromPath(orgPath) with { Name = "TestOrg Updated", NodeType = "Group" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);
        receivedChanges[1].ChangeType.Should().Be(QueryChangeType.Updated);

        // Create a child — should NOT trigger for exact-path query
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{orgPath}/Project1") with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveQuery_ScopeChildren_OnlyNotifiesDirectChildren()
    {
        var proj1 = NodePath("Project1");
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(proj1) with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{_ns}"))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Create a grandchild — should NOT trigger for namespace query
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{proj1}/Task1") with { Name = "Task 1", NodeType = "Code" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task ObserveQuery_WithFilter_IgnoresNonMatchingNodes()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        // Create a non-matching node (different NodeType)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Task1")) with { Name = "Task 1", NodeType = "Code" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(2);

        subscription.Dispose();
    }

    #endregion

    #region Move Tests

    [Fact]
    public async Task ObserveQuery_MoveNode_EmitsDeleteAndCreate()
    {
        var proj1 = NodePath("Project1");
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(proj1) with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);
        receivedChanges[0].Items.Should().HaveCount(1);

        var movedPath = NodePath("Project1Moved");
        await Mesh.Observe(new MoveNodeRequest(proj1, movedPath), o => o).FirstAsync().ToTask();
        await Task.Delay(300);

        receivedChanges.Count.Should().BeGreaterThanOrEqualTo(2);

        var movedNode = await ReadNodeAsync(movedPath);
        movedNode.Should().NotBeNull();
        movedNode!.Name.Should().Be("Project 1");

        var oldNode = await ReadNodeAsync(proj1);
        oldNode.Should().BeNull();

        subscription.Dispose();
    }

    #endregion

    #region Version Tests

    [Fact]
    public async Task ObserveQuery_VersionIncrementsOnEachChange()
    {
        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" });
        await Task.Delay(300);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCountGreaterThanOrEqualTo(2);
        for (int i = 1; i < receivedChanges.Count; i++)
            receivedChanges[i].Version.Should().BeGreaterThan(receivedChanges[i - 1].Version);

        subscription.Dispose();
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task ObserveQuery_DisposalStopsNotifications()
    {
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project1")) with { Name = "Project 1", NodeType = "Markdown" });

        var receivedChanges = new List<QueryResultChange<MeshNode>>();
        var subscription = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(QueryFilter("nodeType:Markdown")))
            .Subscribe(change => receivedChanges.Add(change));

        await Task.Delay(200);
        receivedChanges.Should().HaveCount(1);

        subscription.Dispose();

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(NodePath("Project2")) with { Name = "Project 2", NodeType = "Markdown" });
        await Task.Delay(300);

        receivedChanges.Should().HaveCount(1);
    }

    #endregion
}
