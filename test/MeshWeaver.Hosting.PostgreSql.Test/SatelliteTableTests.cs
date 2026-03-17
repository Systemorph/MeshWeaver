using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests for satellite tables (access, annotations, threads) and the access trigger
/// that rebuilds user_effective_permissions when AccessAssignment nodes change.
/// Uses a partitioned schema with StandardTableMappings for satellite table routing.
/// </summary>
[Collection("PostgreSql")]
public class SatelliteTableTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private Npgsql.NpgsqlDataSource _schemaDs = null!;
    private PostgreSqlStorageAdapter _adapter = null!;

    private static readonly PartitionDefinition TestPartition = new()
    {
        Namespace = "TestOrg",
        DataSource = "default",
        Schema = "satellite_test",
        Versioned = true,
        TableMappings = PartitionDefinition.StandardTableMappings,
    };

    public SatelliteTableTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
            "satellite_test", TestPartition, TestContext.Current.CancellationToken);
        _schemaDs = ds;
        _adapter = adapter;
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<List<(string Permission, bool IsAllow)>> GetEffectivePermissionsAsync(
        string userId, System.Threading.CancellationToken ct)
    {
        var result = new List<(string, bool)>();
        await using var cmd = _schemaDs.CreateCommand(
            $"SELECT permission, is_allow FROM user_effective_permissions WHERE user_id = '{userId}'");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetString(0), reader.GetBoolean(1)));
        return result;
    }

    #region AccessAssignment trigger tests

    [Fact(Timeout = 30000)]
    public async Task AccessAssignment_Triggers_EffectivePermissions_Rebuild()
    {
        var ct = TestContext.Current.CancellationToken;

        var accessNode = new MeshNode("alice_Access", "TestOrg/_Access")
        {
            Name = "Admin role for Alice",
            NodeType = "AccessAssignment",
            MainNode = "TestOrg",
            Content = new
            {
                accessObject = "alice",
                roles = new[] { new { role = "Admin" } }
            }
        };
        await _adapter.WriteAsync(accessNode, _options, ct);

        var permissions = await GetEffectivePermissionsAsync("alice", ct);
        var allowed = permissions.FindAll(p => p.IsAllow).ConvertAll(p => p.Permission);

        allowed.Should().Contain("Read");
        allowed.Should().Contain("Create");
        allowed.Should().Contain("Update");
        allowed.Should().Contain("Delete");
        allowed.Should().Contain("Comment");
    }

    [Fact(Timeout = 30000)]
    public async Task AccessAssignment_With_Denied_Role_Creates_Deny_Permissions()
    {
        var ct = TestContext.Current.CancellationToken;

        var accessNode = new MeshNode("bob_Denied", "TestOrg/_Access")
        {
            Name = "Denied Viewer for Bob",
            NodeType = "AccessAssignment",
            MainNode = "TestOrg",
            Content = new
            {
                accessObject = "bob",
                roles = new[] { new { role = "Viewer", denied = true } }
            }
        };
        await _adapter.WriteAsync(accessNode, _options, ct);

        var permissions = await GetEffectivePermissionsAsync("bob", ct);
        permissions.Should().NotBeEmpty("denied Viewer role should create permission entries");
        permissions.Should().Contain(p => p.Permission == "Read" && p.IsAllow == false,
            "denied Viewer role should create Read permission with is_allow = false");
    }

    [Fact(Timeout = 30000)]
    public async Task Deleting_AccessAssignment_Removes_Permissions()
    {
        var ct = TestContext.Current.CancellationToken;

        var accessNode = new MeshNode("carol_Access", "TestOrg/_Access")
        {
            Name = "Editor role for Carol",
            NodeType = "AccessAssignment",
            MainNode = "TestOrg",
            Content = new
            {
                accessObject = "carol",
                roles = new[] { new { role = "Editor" } }
            }
        };
        await _adapter.WriteAsync(accessNode, _options, ct);

        var before = await GetEffectivePermissionsAsync("carol", ct);
        before.Should().NotBeEmpty("carol should have permissions after AccessAssignment insert");

        await _adapter.DeleteAsync("TestOrg/_Access/carol_Access", ct);

        var after = await GetEffectivePermissionsAsync("carol", ct);
        after.Should().BeEmpty("carol should have no permissions after AccessAssignment deletion");
    }

    #endregion

    #region Thread satellite table tests

    [Fact(Timeout = 30000)]
    public async Task Thread_Written_To_Threads_Satellite_Table()
    {
        var ct = TestContext.Current.CancellationToken;

        var thread = new MeshNode("thread1", "TestOrg/_Thread")
        {
            Name = "Discussion Thread",
            NodeType = "Thread",
            MainNode = "TestOrg",
            Content = new { ParentPath = "TestOrg" }
        };
        await _adapter.WriteAsync(thread, _options, ct);

        // Verify it's in the threads table
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM threads WHERE namespace = 'TestOrg/_Thread' AND id = 'thread1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1, "Thread should be in the threads table");

        // Verify it is NOT in mesh_nodes
        await using var mnCmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'TestOrg/_Thread' AND id = 'thread1'");
        var mnCount = (long)(await mnCmd.ExecuteScalarAsync(ct))!;
        mnCount.Should().Be(0, "Thread should NOT be in mesh_nodes");

        var read = await _adapter.ReadAsync("TestOrg/_Thread/thread1", _options, ct);
        read.Should().NotBeNull();
        read!.Name.Should().Be("Discussion Thread");
    }

    #endregion

    #region Comment satellite table tests

    [Fact(Timeout = 30000)]
    public async Task Comment_Written_To_Annotations_Satellite_Table()
    {
        var ct = TestContext.Current.CancellationToken;

        var comment = new MeshNode("comment1", "TestOrg/SomeDoc/_Comment")
        {
            Name = "Great document!",
            NodeType = "Comment",
            MainNode = "TestOrg/SomeDoc",
            Content = new { Author = "alice", Text = "Great document!" }
        };
        await _adapter.WriteAsync(comment, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM annotations WHERE namespace = 'TestOrg/SomeDoc/_Comment' AND id = 'comment1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1, "Comment should be in the annotations table");

        await using var mnCmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'TestOrg/SomeDoc/_Comment' AND id = 'comment1'");
        var mnCount = (long)(await mnCmd.ExecuteScalarAsync(ct))!;
        mnCount.Should().Be(0, "Comment should NOT be in mesh_nodes");

        var read = await _adapter.ReadAsync("TestOrg/SomeDoc/_Comment/comment1", _options, ct);
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("Comment");
    }

    #endregion

    #region TrackedChange satellite table tests

    [Fact(Timeout = 30000)]
    public async Task TrackedChange_Written_To_Annotations_Satellite_Table()
    {
        var ct = TestContext.Current.CancellationToken;

        var change = new MeshNode("change1", "TestOrg/SomeDoc/_Tracking")
        {
            Name = "Section 3 updated",
            NodeType = "TrackedChange",
            MainNode = "TestOrg/SomeDoc",
            Content = new { Author = "bob", ChangeType = "Edit" }
        };
        await _adapter.WriteAsync(change, _options, ct);

        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM annotations WHERE namespace = 'TestOrg/SomeDoc/_Tracking' AND id = 'change1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1, "TrackedChange should be in the annotations table");

        await using var mnCmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'TestOrg/SomeDoc/_Tracking' AND id = 'change1'");
        var mnCount = (long)(await mnCmd.ExecuteScalarAsync(ct))!;
        mnCount.Should().Be(0, "TrackedChange should NOT be in mesh_nodes");

        var read = await _adapter.ReadAsync("TestOrg/SomeDoc/_Tracking/change1", _options, ct);
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("TrackedChange");
    }

    #endregion
}
