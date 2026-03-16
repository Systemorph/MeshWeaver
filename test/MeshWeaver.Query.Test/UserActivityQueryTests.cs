using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
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
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    private static string P([CallerMemberName] string name = "") => name;

    /// <summary>
    /// The exact query used in UserActivityLayoutAreas.BuildChildren for "My Items".
    /// Must return only main content nodes — no Thread, ThreadMessage, or AccessAssignment.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MyItems_ExcludesThreads()
    {
        var p = P();

        // Create main content nodes (both Markdown — Code is a satellite type excluded from search)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/doc1") with
        {
            Name = "My Document",
            NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/doc2") with
        {
            Name = "My Notes",
            NodeType = "Markdown"
        });

        // Create a Thread satellite (satellite type → MainNode auto-set to namespace)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Thread/thread1") with
        {
            Name = "Discussion Thread",
            NodeType = "Thread"
        });

        // Query: the exact "My Items" query from UserActivityLayoutAreas.BuildChildren
        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{p} is:main context:search scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Threads must NOT appear
        results.Should().HaveCount(2);
        results.Select(n => n.NodeType).Should().NotContain("Thread");
        results.Select(n => n.Name).Should().Contain(["My Document", "My Notes"]);
    }

    [Fact(Timeout = 30000)]
    public async Task MyItems_ExcludesThreadMessages()
    {
        var p = P();

        // Main content
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/notes") with
        {
            Name = "Notes",
            NodeType = "Markdown"
        });

        // Thread + ThreadMessage satellites
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Thread/t1") with
        {
            Name = "Thread 1",
            NodeType = "Thread"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Thread/t1/msg1") with
        {
            Name = "Message 1",
            NodeType = "ThreadMessage"
        });

        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{p} is:main context:search scope:descendants sort:LastModified-desc")
            .ToListAsync();

        results.Should().ContainSingle();
        results[0].Name.Should().Be("Notes");
        results.Select(n => n.NodeType).Should().NotContain("Thread");
        results.Select(n => n.NodeType).Should().NotContain("ThreadMessage");
    }

    [Fact(Timeout = 30000)]
    public async Task MyItems_ExcludesAccessAssignments()
    {
        var p = P();

        // Main content
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/project") with
        {
            Name = "My Project",
            NodeType = "Markdown"
        });

        // AccessAssignment satellite
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Access/user1_Access") with
        {
            Name = "User1 Access",
            NodeType = "AccessAssignment",
            Content = new AccessAssignment
            {
                AccessObject = "user1",
                DisplayName = "User 1",
                Roles = [new RoleAssignment { Role = "Reader" }]
            }
        });

        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{p} is:main context:search scope:descendants sort:LastModified-desc")
            .ToListAsync();

        results.Should().ContainSingle();
        results[0].Name.Should().Be("My Project");
        results.Select(n => n.NodeType).Should().NotContain("AccessAssignment");
    }

    /// <summary>
    /// Comprehensive test: create a realistic user namespace with multiple satellite types
    /// and verify the "My Items" query returns only main content.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MyItems_OnlyReturnsMainContentNodes()
    {
        var p = P();

        // Main content nodes (Code is a satellite type excluded from search, so use Markdown for both)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/doc") with
        {
            Name = "Document", NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/notes") with
        {
            Name = "Notes", NodeType = "Markdown"
        });

        // Satellite: Thread
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Thread/t1") with
        {
            Name = "Thread", NodeType = "Thread"
        });

        // Satellite: ThreadMessage
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Thread/t1/m1") with
        {
            Name = "Message", NodeType = "ThreadMessage"
        });

        // Satellite: AccessAssignment
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Access/a1") with
        {
            Name = "Access", NodeType = "AccessAssignment",
            Content = new AccessAssignment { AccessObject = "u1", Roles = [new RoleAssignment { Role = "Reader" }] }
        });

        // Satellite: Activity log
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_activity/log1") with
        {
            Name = "Activity", NodeType = "Activity",
            MainNode = p,
            Content = new ActivityLog("DataUpdate") { HubPath = p }
        });

        // Satellite: Comment
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/doc/_Comment/c1") with
        {
            Name = "Comment", NodeType = "Comment",
            MainNode = $"{p}/doc"
        });

        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{p} is:main context:search scope:descendants sort:LastModified-desc")
            .ToListAsync();

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
    public async Task IsMain_ExcludesNodesWithDifferentMainNode()
    {
        var p = P();

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/main") with
        {
            Name = "Main Node", NodeType = "Markdown"
        });

        // Manually create a node with MainNode pointing elsewhere
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/satellite") with
        {
            Name = "Satellite Node", NodeType = "Markdown",
            MainNode = $"{p}/main"
        });

        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{p} is:main scope:descendants")
            .ToListAsync();

        results.Should().ContainSingle();
        results[0].Name.Should().Be("Main Node");
    }

    /// <summary>
    /// Test the activity feed query (source:activity) also excludes satellites.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ActivityFeed_ExcludesSatelliteNodeTypes()
    {
        var p = P();

        // Main content node with activity log
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/project") with
        {
            Name = "Project", NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/project/_activity/log1") with
        {
            Name = "Project Activity", NodeType = "Activity",
            MainNode = $"{p}/project",
            Content = new ActivityLog("DataUpdate") { HubPath = $"{p}/project" }
        });

        // AccessAssignment with activity log — should NOT appear in activity feed
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Access/a1") with
        {
            Name = "Access", NodeType = "AccessAssignment",
            Content = new AccessAssignment { AccessObject = "u1", Roles = [new RoleAssignment { Role = "Reader" }] }
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Access/a1/_activity/log1") with
        {
            Name = "Access Activity", NodeType = "Activity",
            MainNode = $"{p}/_Access/a1",
            Content = new ActivityLog("DataUpdate") { HubPath = $"{p}/_Access/a1" }
        });

        // source:activity auto-sets is:main=true
        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"source:activity namespace:{p} scope:descendants")
            .ToListAsync();

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
    public async Task ActivityQuery_ReturnsMainNodeWithActivityLog()
    {
        var p = P();

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/doc") with
        {
            Name = "Document", NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/doc/_activity/log1") with
        {
            Name = "Edit activity", NodeType = "Activity",
            MainNode = $"{p}/doc",
            Content = new ActivityLog("DataUpdate") { HubPath = $"{p}/doc" }
        });

        var results = await MeshQuery.QueryAsync<MeshNode>(
            $"source:activity namespace:{p} scope:descendants")
            .ToListAsync();

        results.Should().ContainSingle();
        results[0].Name.Should().Be("Document");
    }

    /// <summary>
    /// When a data change occurs on an AccessAssignment node, no activity log should be saved
    /// because it's a satellite type (MainNode != Path after auto-setting).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ActivityTracking_SkipsAccessAssignment()
    {
        var p = P();

        // Create parent node first
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(p) with
        {
            Name = "Parent", NodeType = "Markdown"
        });

        // Create AccessAssignment (satellite type)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Access/u1") with
        {
            Name = "User1 Access", NodeType = "AccessAssignment",
            Content = new AccessAssignment { AccessObject = "u1", Roles = [new RoleAssignment { Role = "Reader" }] }
        });

        // Wait for any bundled activity to flush
        await Task.Delay(500);

        // Check: no activity log should exist under the AccessAssignment node
        var activityNodes = await MeshQuery.QueryAsync<MeshNode>(
            $"path:{p}/_Access/u1/_activity scope:descendants")
            .ToListAsync();

        activityNodes.Should().BeEmpty("AccessAssignment is a satellite type and should not have activity logs");
    }

    /// <summary>
    /// Thread nodes are satellite types — activity logs should not be created for them.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ActivityTracking_SkipsThreadNodes()
    {
        var p = P();

        // Create parent
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(p) with
        {
            Name = "Parent", NodeType = "Markdown"
        });

        // Create Thread (satellite type)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Thread/t1") with
        {
            Name = "Discussion", NodeType = "Thread"
        });

        await Task.Delay(500);

        var activityNodes = await MeshQuery.QueryAsync<MeshNode>(
            $"path:{p}/_Thread/t1/_activity scope:descendants")
            .ToListAsync();

        activityNodes.Should().BeEmpty("Thread is a satellite type and should not have activity logs");
    }

    /// <summary>
    /// Verify that the satellite MainNode auto-setting works correctly:
    /// satellite types created via CreateNodeAsync should have MainNode == Namespace (not Path).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SatelliteTypes_HaveMainNodeSetToNamespace()
    {
        var p = P();

        // Create satellite nodes via the normal CreateNodeAsync path
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Thread/t1") with
        {
            Name = "Thread", NodeType = "Thread"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{p}/_Access/a1") with
        {
            Name = "Access", NodeType = "AccessAssignment",
            Content = new AccessAssignment { AccessObject = "u1", Roles = [new RoleAssignment { Role = "Reader" }] }
        });

        // Query with nodeType condition to include satellites in results
        var threadNodes = await MeshQuery.QueryAsync<MeshNode>(
            $"path:{p}/_Thread/t1").ToListAsync();
        threadNodes.Should().ContainSingle();
        var threadNode = threadNodes[0];
        threadNode.MainNode.Should().NotBe(threadNode.Path,
            "Thread is a satellite type — MainNode should be set to namespace, not path");
        threadNode.MainNode.Should().Be($"{p}/_Thread",
            "Thread's MainNode should point to the parent namespace");

        var accessNodes = await MeshQuery.QueryAsync<MeshNode>(
            $"path:{p}/_Access/a1").ToListAsync();
        accessNodes.Should().ContainSingle();
        var accessNode = accessNodes[0];
        accessNode.MainNode.Should().NotBe(accessNode.Path,
            "AccessAssignment is a satellite type — MainNode should be set to namespace, not path");
        accessNode.MainNode.Should().Be($"{p}/_Access",
            "AccessAssignment's MainNode should point to the parent namespace");
    }
}
