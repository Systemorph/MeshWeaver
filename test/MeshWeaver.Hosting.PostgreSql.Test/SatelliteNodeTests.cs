using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests for satellite node types stored in dedicated PostgreSQL tables.
/// Tables: activities, user_activities, threads, access, code, annotations.
/// Verifies table routing, CRUD operations, and cross-table isolation.
/// </summary>
[Collection("PostgreSql")]
public class SatelliteNodeTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private Npgsql.NpgsqlDataSource _schemaDs = null!;
    private PostgreSqlStorageAdapter _mainAdapter = null!;

    private static readonly PartitionDefinition OrgPartition = new()
    {
        Namespace = "ACME",
        DataSource = "default",
        Schema = "acme_sat_test",
        TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
    };

    /// <summary>
    /// Creates an adapter that routes to a specific satellite table.
    /// </summary>
    private PostgreSqlStorageAdapter AdapterFor(string suffix, string tableName)
        => new(_schemaDs,
            partitionDefinition: new PartitionDefinition
            {
                Namespace = "ACME",
                TableMappings = new Dictionary<string, string> { [suffix] = tableName }
            });

    public SatelliteNodeTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
            "acme_sat_test", OrgPartition, TestContext.Current.CancellationToken);
        _schemaDs = ds;
        _mainAdapter = adapter;
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<long> Count(string sql, System.Threading.CancellationToken ct)
        => _schemaDs.ScalarLong(sql, ct).Should().Within(30.Seconds()).Emit();

    #region Activity satellite

    [Fact(Timeout = 30000)]
    public async Task Activity_WriteAndRead_RoutesToActivitiesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var activityAdapter = AdapterFor("_Activity", "activities");

        var activity = new MeshNode("act-1", "ACME/Projects/Alpha/_Activity")
        {
            Name = "Node created",
            NodeType = "Activity",
            MainNode = "ACME/Projects/Alpha",
            Content = new { Action = "Created", UserId = "alice", Timestamp = DateTimeOffset.UtcNow }
        };
        await activityAdapter.Write(activity, _options).Should().Within(30.Seconds()).Emit();

        // Verify in activities table
        (await Count("SELECT COUNT(*) FROM activities WHERE namespace = 'ACME/Projects/Alpha/_Activity' AND id = 'act-1'", ct))
            .Should().Be(1);

        // Read back
        var read = await activityAdapter.Read("ACME/Projects/Alpha/_Activity/act-1", _options)
            .Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("Activity");
        read.MainNode.Should().Be("ACME/Projects/Alpha");
    }

    #endregion

    #region UserActivity satellite

    [Fact(Timeout = 30000)]
    public async Task UserActivity_WriteAndRead_RoutesToUserActivitiesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var uaAdapter = AdapterFor("_UserActivity", "user_activities");

        var ua = new MeshNode("ACME_Projects_Alpha", "User/alice/_UserActivity")
        {
            Name = "Alpha project",
            NodeType = "UserActivity",
            MainNode = "User/alice",
            Content = new UserActivityRecord
            {
                Id = "ACME_Projects_Alpha",
                NodePath = "ACME/Projects/Alpha",
                UserId = "alice",
                ActivityType = ActivityType.Read,
                FirstAccessedAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastAccessedAt = DateTimeOffset.UtcNow,
                AccessCount = 3
            }
        };
        await uaAdapter.Write(ua, _options).Should().Within(30.Seconds()).Emit();

        (await Count("SELECT COUNT(*) FROM user_activities WHERE id = 'ACME_Projects_Alpha'", ct)).Should().Be(1);

        var read = await uaAdapter.Read("User/alice/_UserActivity/ACME_Projects_Alpha", _options)
            .Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("UserActivity");
    }

    #endregion

    #region AccessAssignment satellite

    [Fact(Timeout = 30000)]
    public async Task AccessAssignment_WriteAndRead_RoutesToAccessTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var aaAdapter = AdapterFor("_Access", "access");

        var aa = new MeshNode("aa-1", "ACME/Projects/Alpha/_Access")
        {
            Name = "Admin role for Bob",
            NodeType = "AccessAssignment",
            MainNode = "ACME/Projects/Alpha",
            Content = new { UserId = "bob", RoleId = "Admin", AssignedBy = "alice" }
        };
        await aaAdapter.Write(aa, _options).Should().Within(30.Seconds()).Emit();

        (await Count("SELECT COUNT(*) FROM access WHERE id = 'aa-1'", ct)).Should().Be(1);

        var read = await aaAdapter.Read("ACME/Projects/Alpha/_Access/aa-1", _options)
            .Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.Name.Should().Be("Admin role for Bob");
    }

    [Fact(Timeout = 30000)]
    public async Task AccessAssignment_MultipleAssignments_ListCorrectly()
    {
        var aaAdapter = AdapterFor("_Access", "access");
        var ns = "ACME/Projects/Beta/_Access";

        await aaAdapter.Write(new MeshNode("aa-b1", ns)
        {
            Name = "Viewer for carol",
            NodeType = "AccessAssignment",
            MainNode = "ACME/Projects/Beta",
        }, _options).Should().Within(30.Seconds()).Emit();

        await aaAdapter.Write(new MeshNode("aa-b2", ns)
        {
            Name = "Editor for dave",
            NodeType = "AccessAssignment",
            MainNode = "ACME/Projects/Beta",
        }, _options).Should().Within(30.Seconds()).Emit();

        var (paths, _) = await aaAdapter.ListChildPaths(ns).Should().Within(30.Seconds()).Emit();
        paths.Should().HaveCount(2);
    }

    #endregion

    #region Comment satellite (→ annotations)

    [Fact(Timeout = 30000)]
    public async Task Comment_WriteAndRead_RoutesToAnnotationsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var commentAdapter = AdapterFor("_Comment", "annotations");

        var comment = new MeshNode("cmt-1", "ACME/Docs/readme/_Comment")
        {
            Name = "Great document!",
            NodeType = "Comment",
            MainNode = "ACME/Docs/readme",
            Content = new { Author = "alice", Text = "This is really helpful!", CreatedAt = DateTimeOffset.UtcNow }
        };
        await commentAdapter.Write(comment, _options).Should().Within(30.Seconds()).Emit();

        (await Count("SELECT COUNT(*) FROM annotations WHERE id = 'cmt-1'", ct)).Should().Be(1);

        // Not in mesh_nodes
        (await Count("SELECT COUNT(*) FROM mesh_nodes WHERE id = 'cmt-1' AND namespace = 'ACME/Docs/readme/_Comment'", ct))
            .Should().Be(0);
    }

    [Fact(Timeout = 30000)]
    public async Task Comment_Delete_RemovesFromTable()
    {
        var commentAdapter = AdapterFor("_Comment", "annotations");
        var path = "ACME/Docs/guide/_Comment/cmt-del";

        await commentAdapter.Write(new MeshNode("cmt-del", "ACME/Docs/guide/_Comment")
        {
            Name = "Temporary comment",
            NodeType = "Comment",
            MainNode = "ACME/Docs/guide",
        }, _options).Should().Within(30.Seconds()).Emit();

        await commentAdapter.Exists(path).Should().Within(30.Seconds()).Be(true);
        await commentAdapter.Delete(path).Should().Within(30.Seconds()).Emit();
        await commentAdapter.Exists(path).Should().Within(30.Seconds()).Be(false);
    }

    #endregion

    #region TrackedChange satellite (→ annotations)

    [Fact(Timeout = 30000)]
    public async Task TrackedChange_WriteAndRead_RoutesToAnnotationsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var tcAdapter = AdapterFor("_Tracking", "annotations");

        var tc = new MeshNode("tc-1", "ACME/Docs/report/_Tracking")
        {
            Name = "Section 2 updated",
            NodeType = "TrackedChange",
            MainNode = "ACME/Docs/report",
            Content = new { Author = "bob", ChangeType = "Edit", Section = "2" }
        };
        await tcAdapter.Write(tc, _options).Should().Within(30.Seconds()).Emit();

        (await Count("SELECT COUNT(*) FROM annotations WHERE id = 'tc-1'", ct)).Should().Be(1);

        var read = await tcAdapter.Read("ACME/Docs/report/_Tracking/tc-1", _options)
            .Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.Name.Should().Be("Section 2 updated");
    }

    #endregion

    #region Approval satellite (→ annotations)

    [Fact(Timeout = 30000)]
    public async Task Approval_WriteAndRead_RoutesToAnnotationsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var approvalAdapter = AdapterFor("_Approval", "annotations");

        var approval = new MeshNode("apr-1", "ACME/Docs/policy/_Approval")
        {
            Name = "Approved by Carol",
            NodeType = "Approval",
            MainNode = "ACME/Docs/policy",
            Content = new { Approver = "carol", Status = "Approved", ApprovedAt = DateTimeOffset.UtcNow }
        };
        await approvalAdapter.Write(approval, _options).Should().Within(30.Seconds()).Emit();

        (await Count("SELECT COUNT(*) FROM annotations WHERE id = 'apr-1'", ct)).Should().Be(1);
    }

    #endregion

    #region Notification satellite (→ notifications)

    [Fact(Timeout = 30000)]
    public async Task Notification_WriteAndRead_RoutesToNotificationsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifAdapter = AdapterFor("_Notification", "notifications");

        var notification = new MeshNode("notif-1", "ACME/_Thread/chat-abc/_Notification")
        {
            Name = "Thread ready",
            NodeType = "Notification",
            MainNode = "ACME/_Thread/chat-abc",
            Content = new
            {
                Title = "\"chat-abc\" is ready",
                Message = "Response complete.",
                NotificationType = "General",
                IsRead = false,
                CreatedAt = DateTimeOffset.UtcNow,
                TargetNodePath = "ACME/_Thread/chat-abc"
            }
        };
        await notifAdapter.Write(notification, _options).Should().Within(30.Seconds()).Emit();

        // Lands in the dedicated notifications table.
        (await Count("SELECT COUNT(*) FROM notifications WHERE id = 'notif-1'", ct)).Should().Be(1);

        // NOT in mesh_nodes (would indicate a routing regression).
        (await Count("SELECT COUNT(*) FROM mesh_nodes WHERE id = 'notif-1' AND namespace = 'ACME/_Thread/chat-abc/_Notification'", ct))
            .Should().Be(0);

        // Read back via the adapter.
        var read = await notifAdapter.Read("ACME/_Thread/chat-abc/_Notification/notif-1", _options)
            .Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("Notification");
        read.MainNode.Should().Be("ACME/_Thread/chat-abc");
    }

    [Fact(Timeout = 30000)]
    public async Task Notification_NotInAnnotationsTable_ProvesDedicatedRouting()
    {
        // Defensive: confirms a Notification doesn't accidentally land in the
        // annotations table (which Comment/Tracking/Approval share). If the
        // _Notification → notifications mapping is removed from
        // PartitionDefinition.StandardTableMappings, the longest-suffix
        // resolver could fall back to annotations or mesh_nodes — this catches
        // that regression.
        var ct = TestContext.Current.CancellationToken;
        var notifAdapter = AdapterFor("_Notification", "notifications");

        await notifAdapter.Write(new MeshNode("notif-isolated",
            "ACME/Docs/spec/_Notification")
        {
            Name = "Isolated check",
            NodeType = "Notification",
            MainNode = "ACME/Docs/spec",
            Content = new { Title = "Test", Message = "" }
        }, _options).Should().Within(30.Seconds()).Emit();

        (await Count("SELECT COUNT(*) FROM annotations WHERE id = 'notif-isolated'", ct)).Should().Be(0);
        (await Count("SELECT COUNT(*) FROM notifications WHERE id = 'notif-isolated'", ct)).Should().Be(1);
    }

    #endregion

    #region Code content (Source and Test → code table)

    [Fact(Timeout = 30000)]
    public async Task CodeFile_WriteAndRead_RoutesToCodeTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var codeAdapter = AdapterFor("Source", "code");

        var codeNode = new MeshNode("MyClass", "ACME/Projects/Alpha/Source")
        {
            Name = "MyClass.cs",
            NodeType = "Code",
            Content = new { FileName = "MyClass.cs", Language = "csharp", Namespace = "ACME.Projects" }
        };
        await codeAdapter.Write(codeNode, _options).Should().Within(30.Seconds()).Emit();

        // Verify in code table
        (await Count("SELECT COUNT(*) FROM code WHERE namespace = 'ACME/Projects/Alpha/Source' AND id = 'MyClass'", ct))
            .Should().Be(1);

        // Not in mesh_nodes
        (await Count("SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'ACME/Projects/Alpha/Source' AND id = 'MyClass'", ct))
            .Should().Be(0);

        // Read back
        var read = await codeAdapter.Read("ACME/Projects/Alpha/Source/MyClass", _options)
            .Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("Code");
    }

    [Fact(Timeout = 30000)]
    public async Task TestFile_WriteAndRead_AlsoRoutesToCodeTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var testAdapter = AdapterFor("Test", "code");

        var testNode = new MeshNode("MyClassTests", "ACME/Projects/Alpha/Test")
        {
            Name = "MyClassTests.cs",
            NodeType = "Code",
            Content = new { FileName = "MyClassTests.cs", Language = "csharp", Namespace = "ACME.Projects.Tests" }
        };
        await testAdapter.Write(testNode, _options).Should().Within(30.Seconds()).Emit();

        // Verify in code table (same table as Source)
        (await Count("SELECT COUNT(*) FROM code WHERE namespace = 'ACME/Projects/Alpha/Test' AND id = 'MyClassTests'", ct))
            .Should().Be(1);

        // Read back
        var read = await testAdapter.Read("ACME/Projects/Alpha/Test/MyClassTests", _options)
            .Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.Name.Should().Be("MyClassTests.cs");
    }

    #endregion

    #region Cross-table isolation

    [Fact(Timeout = 30000)]
    public async Task SatelliteNodes_DoNotLeakBetweenTables()
    {
        var ct = TestContext.Current.CancellationToken;
        var commentAdapter = AdapterFor("_Comment", "annotations");
        var activityAdapter = AdapterFor("_Activity", "activities");

        // Write to annotations (comment)
        await commentAdapter.Write(new MeshNode("iso-1", "ACME/X/_Comment")
        {
            Name = "A comment",
            NodeType = "Comment",
            MainNode = "ACME/X",
        }, _options).Should().Within(30.Seconds()).Emit();

        // Write to activities
        await activityAdapter.Write(new MeshNode("iso-2", "ACME/X/_Activity")
        {
            Name = "An activity",
            NodeType = "Activity",
            MainNode = "ACME/X",
        }, _options).Should().Within(30.Seconds()).Emit();

        // Comment should NOT be in activities table
        (await Count("SELECT COUNT(*) FROM activities WHERE id = 'iso-1'", ct)).Should().Be(0);

        // Activity should NOT be in annotations table
        (await Count("SELECT COUNT(*) FROM annotations WHERE id = 'iso-2'", ct)).Should().Be(0);

        // Neither should be in mesh_nodes
        (await Count("SELECT COUNT(*) FROM mesh_nodes WHERE id IN ('iso-1', 'iso-2')", ct)).Should().Be(0);
    }

    [Fact(Timeout = 30000)]
    public async Task MainNode_StillWritesToMeshNodes()
    {
        var ct = TestContext.Current.CancellationToken;

        // Write a regular (non-satellite) node via the main adapter
        await _mainAdapter.Write(new MeshNode("proj-1", "ACME/Projects")
        {
            Name = "Alpha Project",
            NodeType = "Project",
        }, _options).Should().Within(30.Seconds()).Emit();

        // Should be in mesh_nodes since the path doesn't match any satellite suffix
        (await Count("SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'ACME/Projects' AND id = 'proj-1'", ct))
            .Should().Be(1);
    }

    #endregion

    #region MainNode column

    [Fact(Timeout = 30000)]
    public async Task SatelliteNode_MainNode_IsPreserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var commentAdapter = AdapterFor("_Comment", "annotations");

        await commentAdapter.Write(new MeshNode("mn-cmt", "ACME/Reports/Q1/_Comment")
        {
            Name = "Review note",
            NodeType = "Comment",
            MainNode = "ACME/Reports/Q1",
        }, _options).Should().Within(30.Seconds()).Emit();

        // Verify main_node column value via raw SQL
        var mainNode = await _schemaDs.Probe(
            "SELECT main_node FROM annotations WHERE id = 'mn-cmt'",
            System.Array.Empty<(string, object)>(),
            rdr => rdr.GetString(0), ct)
            .Should().Within(30.Seconds()).Emit();
        mainNode.Should().Be("ACME/Reports/Q1");
    }

    [Fact(Timeout = 30000)]
    public async Task SatelliteNode_QueryByMainNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var activityAdapter = AdapterFor("_Activity", "activities");

        // Create activities for two different main nodes
        await activityAdapter.Write(new MeshNode("qa-1", "ACME/A/_Activity")
        {
            NodeType = "Activity", MainNode = "ACME/A",
        }, _options).Should().Within(30.Seconds()).Emit();
        await activityAdapter.Write(new MeshNode("qa-2", "ACME/A/_Activity")
        {
            NodeType = "Activity", MainNode = "ACME/A",
        }, _options).Should().Within(30.Seconds()).Emit();
        await activityAdapter.Write(new MeshNode("qa-3", "ACME/B/_Activity")
        {
            NodeType = "Activity", MainNode = "ACME/B",
        }, _options).Should().Within(30.Seconds()).Emit();

        // Query by main_node via raw SQL (this is what the query engine would do)
        (await Count("SELECT COUNT(*) FROM activities WHERE main_node = 'ACME/A'", ct)).Should().Be(2);
        (await Count("SELECT COUNT(*) FROM activities WHERE main_node = 'ACME/B'", ct)).Should().Be(1);
    }

    #endregion

    #region All satellite types route to correct table

    [Fact(Timeout = 30000)]
    public async Task AllSatelliteTypes_RouteToCorrectTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Write one node per satellite type via the main adapter (which has StandardTableMappings)
        var nodes = new (string id, string ns, string nodeType, string expectedTable)[]
        {
            ("rt-act", "ACME/Y/_Activity", "Activity", "activities"),
            ("rt-ua", "ACME/Y/_UserActivity", "UserActivity", "user_activities"),
            ("rt-thr", "ACME/Y/_Thread", "Thread", "threads"),
            ("rt-acc", "ACME/Y/_Access", "AccessAssignment", "access"),
            ("rt-cmt", "ACME/Y/_Comment", "Comment", "annotations"),
            ("rt-trk", "ACME/Y/_Tracking", "TrackedChange", "annotations"),
            ("rt-apr", "ACME/Y/_Approval", "Approval", "annotations"),
            ("rt-src", "ACME/Y/Source", "Code", "code"),
            ("rt-tst", "ACME/Y/Test", "Code", "code"),
        };

        foreach (var (id, ns, nodeType, _) in nodes)
        {
            await _mainAdapter.Write(new MeshNode(id, ns)
            {
                Name = id,
                NodeType = nodeType,
                MainNode = "ACME/Y",
            }, _options).Should().Within(30.Seconds()).Emit();
        }

        // Verify each node is in the expected table and NOT in mesh_nodes
        foreach (var (id, _, _, expectedTable) in nodes)
        {
            (await Count($"SELECT COUNT(*) FROM \"{expectedTable}\" WHERE id = '{id}'", ct))
                .Should().Be(1, $"node '{id}' should be in table '{expectedTable}'");

            (await Count($"SELECT COUNT(*) FROM mesh_nodes WHERE id = '{id}'", ct))
                .Should().Be(0, $"node '{id}' should NOT be in mesh_nodes");
        }
    }

    #endregion

    #region PartitionDefinition.ResolveTable

    [Fact(Timeout = 30000)]
    public void ResolveTable_MatchesSatelliteSuffix()
    {
        var def = new PartitionDefinition
        {
            Namespace = "User",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        def.ResolveTable("User/alice/_Activity/act-1").Should().Be("activities");
        def.ResolveTable("User/alice/_UserActivity/ua-1").Should().Be("user_activities");
        def.ResolveTable("User/alice/_Thread/chat-1").Should().Be("threads");
        def.ResolveTable("User/alice/_Thread/chat-1/_ThreadMessage/msg-1").Should().Be("threads");
        def.ResolveTable("User/alice/_Access/aa-1").Should().Be("access");
        def.ResolveTable("User/alice/_Comment/cmt-1").Should().Be("annotations");
        def.ResolveTable("User/alice/_Tracking/tc-1").Should().Be("annotations");
        def.ResolveTable("User/alice/_Approval/apr-1").Should().Be("annotations");
        def.ResolveTable("User/alice/Source/MyClass").Should().Be("code");
        def.ResolveTable("User/alice/Test/MyTest").Should().Be("code");
    }

    [Fact(Timeout = 30000)]
    public void ResolveTable_DefaultsToMeshNodes_ForNonSatellitePath()
    {
        var def = new PartitionDefinition
        {
            Namespace = "User",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        def.ResolveTable("User/alice").Should().Be("mesh_nodes");
        def.ResolveTable("User/alice/settings").Should().Be("mesh_nodes");
        def.ResolveTable("ACME/Projects/Alpha").Should().Be("mesh_nodes");
    }

    [Fact(Timeout = 30000)]
    public void ResolveTable_NoMappings_AlwaysReturnsMeshNodes()
    {
        var def = new PartitionDefinition { Namespace = "Admin" };

        def.ResolveTable("Admin/Partition/User").Should().Be("mesh_nodes");
        def.ResolveTable("Admin/anything/_Thread").Should().Be("mesh_nodes");
    }

    #endregion
}
