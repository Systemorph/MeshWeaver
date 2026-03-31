using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using Xunit;

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
        TableMappings = PartitionDefinition.StandardTableMappings,
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
        await activityAdapter.WriteAsync(activity, _options, ct);

        // Verify in activities table
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM activities WHERE namespace = 'ACME/Projects/Alpha/_Activity' AND id = 'act-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);

        // Read back
        var read = await activityAdapter.ReadAsync("ACME/Projects/Alpha/_Activity/act-1", _options, ct);
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
        await uaAdapter.WriteAsync(ua, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM user_activities WHERE id = 'ACME_Projects_Alpha'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);

        var read = await uaAdapter.ReadAsync("User/alice/_UserActivity/ACME_Projects_Alpha", _options, ct);
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
        await aaAdapter.WriteAsync(aa, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM access WHERE id = 'aa-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);

        var read = await aaAdapter.ReadAsync("ACME/Projects/Alpha/_Access/aa-1", _options, ct);
        read.Should().NotBeNull();
        read!.Name.Should().Be("Admin role for Bob");
    }

    [Fact(Timeout = 30000)]
    public async Task AccessAssignment_MultipleAssignments_ListCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var aaAdapter = AdapterFor("_Access", "access");
        var ns = "ACME/Projects/Beta/_Access";

        await aaAdapter.WriteAsync(new MeshNode("aa-b1", ns)
        {
            Name = "Viewer for carol",
            NodeType = "AccessAssignment",
            MainNode = "ACME/Projects/Beta",
        }, _options, ct);

        await aaAdapter.WriteAsync(new MeshNode("aa-b2", ns)
        {
            Name = "Editor for dave",
            NodeType = "AccessAssignment",
            MainNode = "ACME/Projects/Beta",
        }, _options, ct);

        var (paths, _) = await aaAdapter.ListChildPathsAsync(ns, ct);
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
        await commentAdapter.WriteAsync(comment, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM annotations WHERE id = 'cmt-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);

        // Not in mesh_nodes
        await using var mnCmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE id = 'cmt-1' AND namespace = 'ACME/Docs/readme/_Comment'");
        var mnCount = (long)(await mnCmd.ExecuteScalarAsync(ct))!;
        mnCount.Should().Be(0);
    }

    [Fact(Timeout = 30000)]
    public async Task Comment_Delete_RemovesFromTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var commentAdapter = AdapterFor("_Comment", "annotations");
        var path = "ACME/Docs/guide/_Comment/cmt-del";

        await commentAdapter.WriteAsync(new MeshNode("cmt-del", "ACME/Docs/guide/_Comment")
        {
            Name = "Temporary comment",
            NodeType = "Comment",
            MainNode = "ACME/Docs/guide",
        }, _options, ct);

        (await commentAdapter.ExistsAsync(path, ct)).Should().BeTrue();
        await commentAdapter.DeleteAsync(path, ct);
        (await commentAdapter.ExistsAsync(path, ct)).Should().BeFalse();
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
        await tcAdapter.WriteAsync(tc, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM annotations WHERE id = 'tc-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);

        var read = await tcAdapter.ReadAsync("ACME/Docs/report/_Tracking/tc-1", _options, ct);
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
        await approvalAdapter.WriteAsync(approval, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM annotations WHERE id = 'apr-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);
    }

    #endregion

    #region Code satellite (_Source and _Test → code)

    [Fact(Timeout = 30000)]
    public async Task CodeFile_WriteAndRead_RoutesToCodeTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var codeAdapter = AdapterFor("_Source", "code");

        var codeNode = new MeshNode("MyClass", "ACME/Projects/Alpha/_Source")
        {
            Name = "MyClass.cs",
            NodeType = "Code",
            MainNode = "ACME/Projects/Alpha",
            Content = new { FileName = "MyClass.cs", Language = "csharp", Namespace = "ACME.Projects" }
        };
        await codeAdapter.WriteAsync(codeNode, _options, ct);

        // Verify in code table
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM code WHERE namespace = 'ACME/Projects/Alpha/_Source' AND id = 'MyClass'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);

        // Not in mesh_nodes
        await using var mnCmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'ACME/Projects/Alpha/_Source' AND id = 'MyClass'");
        var mnCount = (long)(await mnCmd.ExecuteScalarAsync(ct))!;
        mnCount.Should().Be(0);

        // Read back
        var read = await codeAdapter.ReadAsync("ACME/Projects/Alpha/_Source/MyClass", _options, ct);
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("Code");
        read.MainNode.Should().Be("ACME/Projects/Alpha");
    }

    [Fact(Timeout = 30000)]
    public async Task TestFile_WriteAndRead_AlsoRoutesToCodeTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var testAdapter = AdapterFor("_Test", "code");

        var testNode = new MeshNode("MyClassTests", "ACME/Projects/Alpha/_Test")
        {
            Name = "MyClassTests.cs",
            NodeType = "Code",
            MainNode = "ACME/Projects/Alpha",
            Content = new { FileName = "MyClassTests.cs", Language = "csharp", Namespace = "ACME.Projects.Tests" }
        };
        await testAdapter.WriteAsync(testNode, _options, ct);

        // Verify in code table (same table as _Source)
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM code WHERE namespace = 'ACME/Projects/Alpha/_Test' AND id = 'MyClassTests'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);

        // Read back
        var read = await testAdapter.ReadAsync("ACME/Projects/Alpha/_Test/MyClassTests", _options, ct);
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
        await commentAdapter.WriteAsync(new MeshNode("iso-1", "ACME/X/_Comment")
        {
            Name = "A comment",
            NodeType = "Comment",
            MainNode = "ACME/X",
        }, _options, ct);

        // Write to activities
        await activityAdapter.WriteAsync(new MeshNode("iso-2", "ACME/X/_Activity")
        {
            Name = "An activity",
            NodeType = "Activity",
            MainNode = "ACME/X",
        }, _options, ct);

        // Comment should NOT be in activities table
        await using var cmd1 = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM activities WHERE id = 'iso-1'");
        var cnt1 = (long)(await cmd1.ExecuteScalarAsync(ct))!;
        cnt1.Should().Be(0);

        // Activity should NOT be in annotations table
        await using var cmd2 = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM annotations WHERE id = 'iso-2'");
        var cnt2 = (long)(await cmd2.ExecuteScalarAsync(ct))!;
        cnt2.Should().Be(0);

        // Neither should be in mesh_nodes
        await using var cmd3 = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE id IN ('iso-1', 'iso-2')");
        var cnt3 = (long)(await cmd3.ExecuteScalarAsync(ct))!;
        cnt3.Should().Be(0);
    }

    [Fact(Timeout = 30000)]
    public async Task MainNode_StillWritesToMeshNodes()
    {
        var ct = TestContext.Current.CancellationToken;

        // Write a regular (non-satellite) node via the main adapter
        await _mainAdapter.WriteAsync(new MeshNode("proj-1", "ACME/Projects")
        {
            Name = "Alpha Project",
            NodeType = "Project",
        }, _options, ct);

        // Should be in mesh_nodes since the path doesn't match any satellite suffix
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'ACME/Projects' AND id = 'proj-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);
    }

    #endregion

    #region MainNode column

    [Fact(Timeout = 30000)]
    public async Task SatelliteNode_MainNode_IsPreserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var commentAdapter = AdapterFor("_Comment", "annotations");

        await commentAdapter.WriteAsync(new MeshNode("mn-cmt", "ACME/Reports/Q1/_Comment")
        {
            Name = "Review note",
            NodeType = "Comment",
            MainNode = "ACME/Reports/Q1",
        }, _options, ct);

        // Verify main_node column value via raw SQL
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT main_node FROM annotations WHERE id = 'mn-cmt'");
        var mainNode = (string)(await cmd.ExecuteScalarAsync(ct))!;
        mainNode.Should().Be("ACME/Reports/Q1");
    }

    [Fact(Timeout = 30000)]
    public async Task SatelliteNode_QueryByMainNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var activityAdapter = AdapterFor("_Activity", "activities");

        // Create activities for two different main nodes
        await activityAdapter.WriteAsync(new MeshNode("qa-1", "ACME/A/_Activity")
        {
            NodeType = "Activity", MainNode = "ACME/A",
        }, _options, ct);
        await activityAdapter.WriteAsync(new MeshNode("qa-2", "ACME/A/_Activity")
        {
            NodeType = "Activity", MainNode = "ACME/A",
        }, _options, ct);
        await activityAdapter.WriteAsync(new MeshNode("qa-3", "ACME/B/_Activity")
        {
            NodeType = "Activity", MainNode = "ACME/B",
        }, _options, ct);

        // Query by main_node via raw SQL (this is what the query engine would do)
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM activities WHERE main_node = 'ACME/A'");
        var countA = (long)(await cmd.ExecuteScalarAsync(ct))!;
        countA.Should().Be(2);

        await using var cmd2 = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM activities WHERE main_node = 'ACME/B'");
        var countB = (long)(await cmd2.ExecuteScalarAsync(ct))!;
        countB.Should().Be(1);
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
            ("rt-src", "ACME/Y/_Source", "Code", "code"),
            ("rt-tst", "ACME/Y/_Test", "Code", "code"),
        };

        foreach (var (id, ns, nodeType, _) in nodes)
        {
            await _mainAdapter.WriteAsync(new MeshNode(id, ns)
            {
                Name = id,
                NodeType = nodeType,
                MainNode = "ACME/Y",
            }, _options, ct);
        }

        // Verify each node is in the expected table and NOT in mesh_nodes
        foreach (var (id, _, _, expectedTable) in nodes)
        {
            await using var cmd = _schemaDs.CreateCommand(
                $"SELECT COUNT(*) FROM \"{expectedTable}\" WHERE id = '{id}'");
            var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
            count.Should().Be(1, $"node '{id}' should be in table '{expectedTable}'");

            await using var mnCmd = _schemaDs.CreateCommand(
                $"SELECT COUNT(*) FROM mesh_nodes WHERE id = '{id}'");
            var mnCount = (long)(await mnCmd.ExecuteScalarAsync(ct))!;
            mnCount.Should().Be(0, $"node '{id}' should NOT be in mesh_nodes");
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
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        def.ResolveTable("User/alice/_Activity/act-1").Should().Be("activities");
        def.ResolveTable("User/alice/_UserActivity/ua-1").Should().Be("user_activities");
        def.ResolveTable("User/alice/_Thread/chat-1").Should().Be("threads");
        def.ResolveTable("User/alice/_Thread/chat-1/_ThreadMessage/msg-1").Should().Be("threads");
        def.ResolveTable("User/alice/_Access/aa-1").Should().Be("access");
        def.ResolveTable("User/alice/_Comment/cmt-1").Should().Be("annotations");
        def.ResolveTable("User/alice/_Tracking/tc-1").Should().Be("annotations");
        def.ResolveTable("User/alice/_Approval/apr-1").Should().Be("annotations");
        def.ResolveTable("User/alice/_Source/MyClass").Should().Be("code");
        def.ResolveTable("User/alice/_Test/MyTest").Should().Be("code");
    }

    [Fact(Timeout = 30000)]
    public void ResolveTable_DefaultsToMeshNodes_ForNonSatellitePath()
    {
        var def = new PartitionDefinition
        {
            Namespace = "User",
            TableMappings = PartitionDefinition.StandardTableMappings
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
