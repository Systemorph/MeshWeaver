using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Tests for satellite tables (access, annotations, threads) and the access-projection rebuild
/// that refreshes user_effective_permissions when AccessAssignment nodes change.
/// Uses a partitioned schema with StandardTableMappings for satellite table routing.
/// <para>1:1 port of the PG twin. PG rebuilds the projection in the <c>trg_access_changed</c>
/// DB trigger; Snowflake has no triggers, so the SAME rebuild runs in the C# write/delete path
/// (<see cref="SnowflakeStorageAdapter"/>) — the observable behavior asserted here is
/// identical, so the assertions port unchanged.</para>
/// </summary>
[Collection("Snowflake")]
public class SatelliteTableTests : IAsyncLifetime
{
    private const string SchemaName = "satellite_test";

    private readonly SnowflakeFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private SnowflakeConnectionSource _schemaDs = null!;
    private SnowflakeStorageAdapter _adapter = null!;

    private static readonly PartitionDefinition TestPartition = new()
    {
        Namespace = "TestOrg",
        DataSource = "default",
        Schema = SchemaName,
        Versioned = true,
        TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
    };

    public SatelliteTableTests(SnowflakeFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        // Green-skip the whole class when no endpoint is up (xunit v3 honors the dynamic-skip
        // exception from lifetime methods); each test method carries the same first-line guard
        // per the fixture contract.
        _fixture.SkipUnlessAvailable();

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
            SchemaName, TestPartition, TestContext.Current.CancellationToken);
        _schemaDs = ds;
        _adapter = adapter;
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    // Reactive read of user_effective_permissions rows — the multi-row reader (low-level
    // driver op) stays async inside the IObservable wrapper. Unlike the PG twin, the SQL is
    // schema-qualified with quoted-lowercase identifiers (Snowflake uppercases unquoted names
    // and has no search_path).
    private Task<List<(string Permission, bool IsAllow)>> GetEffectivePermissions(
        string userId, System.Threading.CancellationToken ct)
        => _schemaDs.Rows(
            $"SELECT \"permission\", \"is_allow\" FROM {SnowflakeIdentifiers.Qualify(SchemaName, "user_effective_permissions")} " +
            "WHERE \"user_id\" = :uid",
            new[] { ("uid", (object)userId) },
            rdr => (rdr.GetString(0), rdr.GetBoolean(1)), ct)
            .Should().Within(30.Seconds()).Emit();

    #region AccessAssignment trigger tests

    [Fact(Timeout = 30000)]
    public async Task AccessAssignment_Triggers_EffectivePermissions_Rebuild()
    {
        _fixture.SkipUnlessAvailable();
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
        await _adapter.Write(accessNode, _options).Should().Within(30.Seconds()).Emit();

        var permissions = await GetEffectivePermissions("alice", ct);
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
        _fixture.SkipUnlessAvailable();
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
        await _adapter.Write(accessNode, _options).Should().Within(30.Seconds()).Emit();

        var permissions = await GetEffectivePermissions("bob", ct);
        permissions.Should().NotBeEmpty("denied Viewer role should create permission entries");
        permissions.Should().Contain(p => p.Permission == "Read" && p.IsAllow == false,
            "denied Viewer role should create Read permission with is_allow = false");
    }

    [Fact(Timeout = 30000)]
    public async Task Deleting_AccessAssignment_Removes_Permissions()
    {
        _fixture.SkipUnlessAvailable();
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
        await _adapter.Write(accessNode, _options).Should().Within(30.Seconds()).Emit();

        var before = await GetEffectivePermissions("carol", ct);
        before.Should().NotBeEmpty("carol should have permissions after AccessAssignment insert");

        await _adapter.Delete("TestOrg/_Access/carol_Access").Should().Within(30.Seconds()).Emit();

        var after = await GetEffectivePermissions("carol", ct);
        after.Should().BeEmpty("carol should have no permissions after AccessAssignment deletion");
    }

    #endregion

    #region Thread satellite table tests

    [Fact(Timeout = 30000)]
    public async Task Thread_Written_To_Threads_Satellite_Table()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        var thread = new MeshNode("thread1", "TestOrg/_Thread")
        {
            Name = "Discussion Thread",
            NodeType = "Thread",
            MainNode = "TestOrg",
            Content = new { }
        };
        await _adapter.Write(thread, _options).Should().Within(30.Seconds()).Emit();

        // Verify it's in the threads table
        await _schemaDs.ScalarLong(
            $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(SchemaName, "threads")} " +
            "WHERE \"namespace\" = 'TestOrg/_Thread' AND \"id\" = 'thread1'", ct)
            .Should().Within(30.Seconds()).Be(1L, "Thread should be in the threads table");

        // Verify it is NOT in mesh_nodes
        await _schemaDs.ScalarLong(
            $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(SchemaName, "mesh_nodes")} " +
            "WHERE \"namespace\" = 'TestOrg/_Thread' AND \"id\" = 'thread1'", ct)
            .Should().Within(30.Seconds()).Be(0L, "Thread should NOT be in mesh_nodes");

        var read = await _adapter.Read("TestOrg/_Thread/thread1", _options).Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.Name.Should().Be("Discussion Thread");
    }

    #endregion

    #region Comment satellite table tests

    [Fact(Timeout = 30000)]
    public async Task Comment_Written_To_Annotations_Satellite_Table()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        var comment = new MeshNode("comment1", "TestOrg/SomeDoc/_Comment")
        {
            Name = "Great document!",
            NodeType = "Comment",
            MainNode = "TestOrg/SomeDoc",
            Content = new { Author = "alice", Text = "Great document!" }
        };
        await _adapter.Write(comment, _options).Should().Within(30.Seconds()).Emit();

        await _schemaDs.ScalarLong(
            $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(SchemaName, "annotations")} " +
            "WHERE \"namespace\" = 'TestOrg/SomeDoc/_Comment' AND \"id\" = 'comment1'", ct)
            .Should().Within(30.Seconds()).Be(1L, "Comment should be in the annotations table");

        await _schemaDs.ScalarLong(
            $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(SchemaName, "mesh_nodes")} " +
            "WHERE \"namespace\" = 'TestOrg/SomeDoc/_Comment' AND \"id\" = 'comment1'", ct)
            .Should().Within(30.Seconds()).Be(0L, "Comment should NOT be in mesh_nodes");

        var read = await _adapter.Read("TestOrg/SomeDoc/_Comment/comment1", _options).Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("Comment");
    }

    #endregion

    #region TrackedChange satellite table tests

    [Fact(Timeout = 30000)]
    public async Task TrackedChange_Written_To_Annotations_Satellite_Table()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        var change = new MeshNode("change1", "TestOrg/SomeDoc/_Tracking")
        {
            Name = "Section 3 updated",
            NodeType = "TrackedChange",
            MainNode = "TestOrg/SomeDoc",
            Content = new { Author = "bob", ChangeType = "Edit" }
        };
        await _adapter.Write(change, _options).Should().Within(30.Seconds()).Emit();

        await _schemaDs.ScalarLong(
            $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(SchemaName, "annotations")} " +
            "WHERE \"namespace\" = 'TestOrg/SomeDoc/_Tracking' AND \"id\" = 'change1'", ct)
            .Should().Within(30.Seconds()).Be(1L, "TrackedChange should be in the annotations table");

        await _schemaDs.ScalarLong(
            $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(SchemaName, "mesh_nodes")} " +
            "WHERE \"namespace\" = 'TestOrg/SomeDoc/_Tracking' AND \"id\" = 'change1'", ct)
            .Should().Within(30.Seconds()).Be(0L, "TrackedChange should NOT be in mesh_nodes");

        var read = await _adapter.Read("TestOrg/SomeDoc/_Tracking/change1", _options).Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.NodeType.Should().Be("TrackedChange");
    }

    #endregion
}
