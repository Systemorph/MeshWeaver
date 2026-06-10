using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests that the "My Items" query (namespace:{user} is:main context:search scope:descendants)
/// correctly excludes satellite node types: Thread, ThreadMessage, AccessAssignment, etc.
/// Also tests that activity tracking does not create logs for satellite/excluded node types.
/// </summary>
public class UserActivityQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Each [Fact] uses a per-method partition prefix via P(), so SP-sharing
    // is collision-safe.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    private static string P([CallerMemberName] string name = "") => name;

    /// <summary>
    /// The exact query used in UserActivityLayoutAreas.BuildChildren for "My Items".
    /// Must return only main content nodes — no Thread, ThreadMessage, or AccessAssignment.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void MyItems_ExcludesThreads()
    {
        var p = P();

        // Create main content nodes (both Markdown — Code is a satellite type excluded from search)
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/doc1") with
        {
            Name = "My Document",
            NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/doc2") with
        {
            Name = "My Notes",
            NodeType = "Markdown"
        }).Should().Emit();

        // Create a Thread satellite (satellite type → MainNode auto-set to namespace)
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Thread/thread1") with
        {
            Name = "Discussion Thread",
            NodeType = "Thread"
        }).Should().Emit();

        // Query: the exact "My Items" query from UserActivityLayoutAreas.BuildChildren
        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{p} is:main context:search scope:descendants sort:LastModified-desc")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        // Threads must NOT appear
        results.Should().HaveCount(2);
        results.Select(n => n.NodeType).Should().NotContain("Thread");
        results.Select(n => n.Name).Should().Contain(["My Document", "My Notes"]);
    }

    [Fact(Timeout = 30000)]
    public void MyItems_ExcludesThreadMessages()
    {
        var p = P();

        // Main content
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/notes") with
        {
            Name = "Notes",
            NodeType = "Markdown"
        }).Should().Emit();

        // Thread + ThreadMessage satellites
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Thread/t1") with
        {
            Name = "Thread 1",
            NodeType = "Thread"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Thread/t1/msg1") with
        {
            Name = "Message 1",
            NodeType = "ThreadMessage"
        }).Should().Emit();

        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{p} is:main context:search scope:descendants sort:LastModified-desc")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        results.Should().ContainSingle();
        results[0].Name.Should().Be("Notes");
        results.Select(n => n.NodeType).Should().NotContain("Thread");
        results.Select(n => n.NodeType).Should().NotContain("ThreadMessage");
    }

    [Fact(Timeout = 30000)]
    public void MyItems_ExcludesAccessAssignments()
    {
        var p = P();

        // Main content
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/project") with
        {
            Name = "My Project",
            NodeType = "Markdown"
        }).Should().Emit();

        // AccessAssignment satellite
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Access/user1_Access") with
        {
            Name = "User1 Access",
            NodeType = "AccessAssignment",
            Content = new AccessAssignment
            {
                AccessObject = "user1",
                DisplayName = "User 1",
                Roles = [new RoleAssignment { Role = "Reader" }]
            }
        }).Should().Emit();

        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{p} is:main context:search scope:descendants sort:LastModified-desc")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        results.Should().ContainSingle();
        results[0].Name.Should().Be("My Project");
        results.Select(n => n.NodeType).Should().NotContain("AccessAssignment");
    }

    /// <summary>
    /// Comprehensive test: create a realistic user namespace with multiple satellite types
    /// and verify the "My Items" query returns only main content.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void MyItems_OnlyReturnsMainContentNodes()
    {
        var p = P();

        // Main content nodes (Code is a satellite type excluded from search, so use Markdown for both)
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/doc") with
        {
            Name = "Document", NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/notes") with
        {
            Name = "Notes", NodeType = "Markdown"
        }).Should().Emit();

        // Satellite: Thread
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Thread/t1") with
        {
            Name = "Thread", NodeType = "Thread"
        }).Should().Emit();

        // Satellite: ThreadMessage
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Thread/t1/m1") with
        {
            Name = "Message", NodeType = "ThreadMessage"
        }).Should().Emit();

        // Satellite: AccessAssignment
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Access/a1") with
        {
            Name = "Access", NodeType = "AccessAssignment",
            Content = new AccessAssignment { AccessObject = "u1", Roles = [new RoleAssignment { Role = "Reader" }] }
        }).Should().Emit();

        // Satellite: Activity log
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_activity/log1") with
        {
            Name = "Activity", NodeType = "Activity",
            MainNode = p,
            Content = new ActivityLog("DataUpdate") { HubPath = p }
        }).Should().Emit();

        // Satellite: Comment
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/doc/_Comment/c1") with
        {
            Name = "Comment", NodeType = "Comment",
            MainNode = $"{p}/doc"
        }).Should().Emit();

        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{p} is:main context:search scope:descendants sort:LastModified-desc")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        // Only the two main content nodes should appear
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Document", "Notes"]);
        var nodeTypes = results.Select(n => n.NodeType).ToList();
        nodeTypes.Should().AllBe("Markdown");
        nodeTypes.Should().NotContain("Thread");
        nodeTypes.Should().NotContain("ThreadMessage");
        nodeTypes.Should().NotContain("AccessAssignment");
        nodeTypes.Should().NotContain("Activity");
        nodeTypes.Should().NotContain("Comment");
    }

    /// <summary>
    /// Verifies that is:main filtering actually checks MainNode == Path.
    /// Nodes with explicit MainNode != Path should be excluded.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void IsMain_ExcludesNodesWithDifferentMainNode()
    {
        var p = P();

        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/main") with
        {
            Name = "Main Node", NodeType = "Markdown"
        }).Should().Emit();

        // Manually create a node with MainNode pointing elsewhere
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/satellite") with
        {
            Name = "Satellite Node", NodeType = "Markdown",
            MainNode = $"{p}/main"
        }).Should().Emit();

        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{p} is:main scope:descendants")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        results.Should().ContainSingle();
        results[0].Name.Should().Be("Main Node");
    }

    /// <summary>
    /// Test the activity feed query (source:activity) also excludes satellites.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ActivityFeed_ExcludesSatelliteNodeTypes()
    {
        var p = P();

        // Main content node with activity log
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/project") with
        {
            Name = "Project", NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/project/_activity/log1") with
        {
            Name = "Project Activity", NodeType = "Activity",
            MainNode = $"{p}/project",
            Content = new ActivityLog("DataUpdate") { HubPath = $"{p}/project" }
        }).Should().Emit();

        // AccessAssignment with activity log — should NOT appear in activity feed
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Access/a1") with
        {
            Name = "Access", NodeType = "AccessAssignment",
            Content = new AccessAssignment { AccessObject = "u1", Roles = [new RoleAssignment { Role = "Reader" }] }
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Access/a1/_activity/log1") with
        {
            Name = "Access Activity", NodeType = "Activity",
            MainNode = $"{p}/_Access/a1",
            Content = new ActivityLog("DataUpdate") { HubPath = $"{p}/_Access/a1" }
        }).Should().Emit();

        // source:activity auto-sets is:main=true
        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"source:activity namespace:{p} scope:descendants")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        // Only the main content node should appear, not AccessAssignment
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Project");
        results[0].NodeType.Should().Be("Markdown");
    }
}

/// <summary>
/// Tests that the ActivityLogBundler (data change activity tracking) skips
/// satellite nodes and excluded node types like AccessAssignment.
/// </summary>
public class ActivityTrackingFilterTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    private static string P([CallerMemberName] string name = "") => name;

    /// <summary>
    /// Manually created activity logs for main content nodes should be queryable
    /// via source:activity (verifies the query pipeline, not the bundler).
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ActivityQuery_ReturnsMainNodeWithActivityLog()
    {
        var p = P();

        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/doc") with
        {
            Name = "Document", NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/doc/_activity/log1") with
        {
            Name = "Edit activity", NodeType = "Activity",
            MainNode = $"{p}/doc",
            Content = new ActivityLog("DataUpdate") { HubPath = $"{p}/doc" }
        }).Should().Emit();

        var results = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"source:activity namespace:{p} scope:descendants")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        results.Should().ContainSingle();
        results[0].Name.Should().Be("Document");
    }

    /// <summary>
    /// When a data change occurs on an AccessAssignment node, no activity log should be saved
    /// because it's a satellite type (MainNode != Path after auto-setting).
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ActivityTracking_SkipsAccessAssignment()
    {
        var p = P();

        // Create parent node first (top-level partition root → seed under System)
        SeedTopLevel(MeshNode.FromPath(p) with
        {
            Name = "Parent", NodeType = "Markdown"
        });

        // Create AccessAssignment (satellite type)
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Access/u1") with
        {
            Name = "User1 Access", NodeType = "AccessAssignment",
            Content = new AccessAssignment { AccessObject = "u1", Roles = [new RoleAssignment { Role = "Reader" }] }
        }).Should().Emit();

        // Check: no activity log should ever appear under the AccessAssignment node.
        // Negative assertion — flatten the live query's items and assert nothing arrives.
        MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{p}/_Access/u1/_activity scope:descendants"))
            .SelectMany(c => c.Items)
            .Should().NotEmit(within: TimeSpan.FromSeconds(2),
                "AccessAssignment is a satellite type and should not have activity logs");
    }

    /// <summary>
    /// Thread nodes are satellite types — activity logs should not be created for them.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ActivityTracking_SkipsThreadNodes()
    {
        var p = P();

        // Create parent (top-level partition root → seed under System)
        SeedTopLevel(MeshNode.FromPath(p) with
        {
            Name = "Parent", NodeType = "Markdown"
        });

        // Create Thread (satellite type)
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Thread/t1") with
        {
            Name = "Discussion", NodeType = "Thread"
        }).Should().Emit();

        // Negative assertion — no activity log should ever appear under the Thread node.
        MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{p}/_Thread/t1/_activity scope:descendants"))
            .SelectMany(c => c.Items)
            .Should().NotEmit(within: TimeSpan.FromSeconds(2),
                "Thread is a satellite type and should not have activity logs");
    }

    /// <summary>
    /// Verify that the satellite MainNode auto-setting works correctly:
    /// satellite types created via NodeFactory.CreateNode should have MainNode == Namespace (not Path).
    /// </summary>
    [Fact(Timeout = 30000)]
    public void SatelliteTypes_HaveMainNodeSetToNamespace()
    {
        var p = P();

        // Create satellite nodes via the normal NodeFactory.CreateNode path
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Thread/t1") with
        {
            Name = "Thread", NodeType = "Thread"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath($"{p}/_Access/a1") with
        {
            Name = "Access", NodeType = "AccessAssignment",
            Content = new AccessAssignment { AccessObject = "u1", Roles = [new RoleAssignment { Role = "Reader" }] }
        }).Should().Emit();

        // Query with nodeType condition to include satellites in results
        var threadNodes = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{p}/_Thread/t1")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;
        threadNodes.Should().ContainSingle();
        var threadNode = threadNodes[0];
        threadNode.MainNode.Should().NotBe(threadNode.Path,
            "Thread is a satellite type — MainNode should be set to namespace, not path");
        threadNode.MainNode.Should().Be($"{p}/_Thread",
            "Thread's MainNode should point to the parent namespace");

        var accessNodes = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{p}/_Access/a1")).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;
        accessNodes.Should().ContainSingle();
        var accessNode = accessNodes[0];
        accessNode.MainNode.Should().NotBe(accessNode.Path,
            "AccessAssignment is a satellite type — MainNode should be set to namespace, not path");
        accessNode.MainNode.Should().Be($"{p}/_Access",
            "AccessAssignment's MainNode should point to the parent namespace");
    }
}
