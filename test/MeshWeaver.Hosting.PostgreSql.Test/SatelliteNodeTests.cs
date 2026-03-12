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
/// Covers Activity, UserActivity, AccessAssignment, Comment, TrackedChange, Approval.
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
    public async Task AccessAssignment_WriteAndRead_RoutesToTable()
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

    #region Comment satellite

    [Fact(Timeout = 30000)]
    public async Task Comment_WriteAndRead_RoutesToCommentsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var commentAdapter = AdapterFor("_Comment", "comments");

        var comment = new MeshNode("cmt-1", "ACME/Docs/readme/_Comment")
        {
            Name = "Great document!",
            NodeType = "Comment",
            MainNode = "ACME/Docs/readme",
            Content = new { Author = "alice", Text = "This is really helpful!", CreatedAt = DateTimeOffset.UtcNow }
        };
        await commentAdapter.WriteAsync(comment, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM comments WHERE id = 'cmt-1'");
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
        var commentAdapter = AdapterFor("_Comment", "comments");
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

    #region TrackedChange satellite

    [Fact(Timeout = 30000)]
    public async Task TrackedChange_WriteAndRead_RoutesToTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var tcAdapter = AdapterFor("_Tracking", "tracking");

        var tc = new MeshNode("tc-1", "ACME/Docs/report/_Tracking")
        {
            Name = "Section 2 updated",
            NodeType = "TrackedChange",
            MainNode = "ACME/Docs/report",
            Content = new { Author = "bob", ChangeType = "Edit", Section = "2" }
        };
        await tcAdapter.WriteAsync(tc, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM tracking WHERE id = 'tc-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);

        var read = await tcAdapter.ReadAsync("ACME/Docs/report/_Tracking/tc-1", _options, ct);
        read.Should().NotBeNull();
        read!.Name.Should().Be("Section 2 updated");
    }

    #endregion

    #region Approval satellite

    [Fact(Timeout = 30000)]
    public async Task Approval_WriteAndRead_RoutesToTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var approvalAdapter = AdapterFor("_Approval", "approvals");

        var approval = new MeshNode("apr-1", "ACME/Docs/policy/_Approval")
        {
            Name = "Approved by Carol",
            NodeType = "Approval",
            MainNode = "ACME/Docs/policy",
            Content = new { Approver = "carol", Status = "Approved", ApprovedAt = DateTimeOffset.UtcNow }
        };
        await approvalAdapter.WriteAsync(approval, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM approvals WHERE id = 'apr-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);
    }

    #endregion

    #region Cross-table isolation

    [Fact(Timeout = 30000)]
    public async Task SatelliteNodes_DoNotLeakBetweenTables()
    {
        var ct = TestContext.Current.CancellationToken;
        var commentAdapter = AdapterFor("_Comment", "comments");
        var activityAdapter = AdapterFor("_Activity", "activities");

        // Write to comments
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

        // Activity should NOT be in comments table
        await using var cmd2 = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM comments WHERE id = 'iso-2'");
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
        var commentAdapter = AdapterFor("_Comment", "comments");

        await commentAdapter.WriteAsync(new MeshNode("mn-cmt", "ACME/Reports/Q1/_Comment")
        {
            Name = "Review note",
            NodeType = "Comment",
            MainNode = "ACME/Reports/Q1",
        }, _options, ct);

        // Verify main_node column value via raw SQL
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT main_node FROM comments WHERE id = 'mn-cmt'");
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

    #region PartitionDefinition.ResolveTable

    [Fact(Timeout = 30000)]
    public void ResolveTable_MatchesSatelliteSuffix()
    {
        var def = new PartitionDefinition
        {
            Namespace = "User",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        def.ResolveTable("User/alice/_Thread/chat-1").Should().Be("threads");
        def.ResolveTable("User/alice/_Thread/chat-1/_ThreadMessage/msg-1").Should().Be("threads");
        def.ResolveTable("User/alice/_Activity/act-1").Should().Be("activities");
        def.ResolveTable("User/alice/_UserActivity/ua-1").Should().Be("user_activities");
        def.ResolveTable("User/alice/_Comment/cmt-1").Should().Be("comments");
        def.ResolveTable("User/alice/_Access/aa-1").Should().Be("access");
        def.ResolveTable("User/alice/_Tracking/tc-1").Should().Be("tracking");
        def.ResolveTable("User/alice/_Approval/apr-1").Should().Be("approvals");
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
