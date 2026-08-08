using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that the first user onboarding flow creates a global admin.
/// The first user should receive Admin role at Admin/_Access (stored in admin.access table),
/// giving them full permissions across all partitions.
///
/// Bug: Onboarding.razor was calling AddUserRoleAsync(username, "PlatformAdmin", "Admin", username)
/// â€” correct namespace but wrong role ("PlatformAdmin" instead of "Admin").
/// Fix: AddUserRoleAsync(username, Role.Admin.Id, "Admin", username)
/// </summary>
[Collection("PostgreSql")]
public class FirstUserOnboardingTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public FirstUserOnboardingTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Simulates the first-user onboarding: creates a User node, then assigns Admin role
    /// at Admin/_Access scope (stored in admin.access table).
    /// Verifies the user gets all permissions and partition_access to the admin partition.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task FirstUser_GetsGlobalAdminRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();

        // Create admin schema (global admin assignments live here)
        var partitionDef = new PartitionDefinition
        {
            Namespace = "Admin",
            Schema = "admin",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (adminDs, adminAdapter) = await _fixture.CreateSchemaAdapter("admin", partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

        // Step 1: Create the User node (simulates NodeFactory.CreateNodeAsync in onboarding).
        // 🚨 Users are partition ROOTS: the User node lives in the user's OWN partition schema
        // (named after the id), namespace="". There is NO shared legacy `user` schema — the
        // pre-V27 access-object schema is gone. Creating a literal `user` schema here leaked into
        // the collection-shared fixture (CleanData only truncates rows, never drops schemas), so a
        // later test in the same run — MigrationUserBackfillFromIndexTests, whose whole premise is
        // "the V05 backfill neither creates nor requires a `user` schema" — saw the stale schema and
        // failed its `information_schema.schemata WHERE schema_name='user' = 0` invariant. Provision
        // the user's own partition instead, matching the live architecture.
        const string username = "firstadmin";
        var (_, userAdapter) = await _fixture.CreateSchemaAdapter(username, null, ct)
            .Should().Within(60.Seconds()).Emit();

        await userAdapter.Write(new MeshNode(username)
        {
            Name = "First Admin",
            NodeType = "User",
            State = MeshNodeState.Active,
            Content = new User
            {
                FullName = "First Admin",
                Email = "admin@example.com"
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Step 2: Assign global Admin role (simulates the fixed onboarding code)
        // → namespace = "Admin/_Access", stored in admin.access table. MainNode is the
        // SCOPE the path encodes — 'Admin' — the only shape AccessAssignmentGuard accepts
        // (an empty MainNode is a ROOT grant; the old 'Admin/_Access' container value
        // projected the grant one level too deep, see migration V49).
        var ns = "Admin/_Access";
        var nodeId = $"{username}_Access";
        await adminAdapter.Write(new MeshNode(nodeId, ns)
        {
            Name = username,
            NodeType = "AccessAssignment",
            State = MeshNodeState.Active,
            MainNode = "Admin",
            Content = new AccessAssignment
            {
                DisplayName = username,
                AccessObject = username,
                Roles = [new RoleAssignment { Role = Role.Admin.Id }]
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Step 3: Rebuild permissions (normally triggered by DB trigger)
        var adminAccessControl = new PostgreSqlAccessControl(adminDs);
        await adminAccessControl.RebuildDenormalizedTableAsync(ct).Run().Should().Within(30.Seconds()).Emit();

        // Verify: user has all permissions at Admin/_Access scope
        var allPermissions = new[] { "Read", "Create", "Update", "Delete", "Comment", "Execute", "Thread" };
        foreach (var perm in allPermissions)
        {
            await adminAccessControl.HasPermissionAsync(username, "Admin/_Access", perm, ct).Run()
                .Should().Within(30.Seconds()).Be(true);
        }

        // Verify: partition_access entry exists for admin partition
        var partitions = await _fixture.DataSource.Rows(
            "SELECT partition FROM public.partition_access WHERE user_id = @uid ORDER BY partition",
            new[] { ("uid", (object)username) },
            reader => reader.GetString(0), ct)
            .Should().Within(30.Seconds()).Emit();

        partitions.Should().Contain("admin",
            "Global admin should have partition_access to the admin partition");
    }

    /// <summary>
    /// Global admin should see all organizations via cross-schema search.
    /// Organizations have PublicRead=true so they're visible to any authenticated user.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task FirstUser_CanSeeAllOrganizations_ViaCrossSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        // Create admin schema with global admin
        var (adminDs, adminAdapter) = await _fixture.CreateSchemaAdapter(
            "admin",
            partitionDef with { Namespace = "Admin", Schema = "admin" }, ct)
            .Should().Within(60.Seconds()).Emit();

        const string username = "globaladmin";
        await adminAdapter.Write(new MeshNode($"{username}_Access", "Admin/_Access")
        {
            Name = username,
            NodeType = "AccessAssignment",
            State = MeshNodeState.Active,
            MainNode = "Admin",
            Content = new AccessAssignment
            {
                DisplayName = username,
                AccessObject = username,
                Roles = [new RoleAssignment { Role = Role.Admin.Id }]
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        var adminAccessControl = new PostgreSqlAccessControl(adminDs);
        await adminAccessControl.RebuildDenormalizedTableAsync(ct).Run().Should().Within(30.Seconds()).Emit();

        // Create 2 org schemas with Organization nodes
        var orgNames = new[] { "OrgAlpha", "OrgBeta" };
        foreach (var org in orgNames)
        {
            var schemaName = org.ToLowerInvariant();
            var (ds, adapter) = await _fixture.CreateSchemaAdapter(
                schemaName, partitionDef with { Namespace = org, Schema = schemaName }, ct)
                .Should().Within(60.Seconds()).Emit();

            await adapter.Write(new MeshNode(org)
            {
                Name = $"{org} Corp",
                NodeType = SpaceNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new Space()
            }, _options).Should().Within(30.Seconds()).Emit();

            // 🔒 #953 — the org root is readable through a Read grant to the well-known `Public`
            // subject (what a PartitionAccessPolicy { PublicRead = true } `_Policy` projects, #603),
            // not through a node-type flag. The partition gate below is unaffected.
            var ac = new PostgreSqlAccessControl(ds);
            await ac.Grant(org, "Public", "Read", isAllow: true, ct)
                .Should().Within(30.Seconds()).Emit();
        }

        // Populate searchable_schemas
        await PopulateSearchableSchemasAsync(orgNames.Select(o => o.ToLowerInvariant()), ct)
            .Run().Should().Within(30.Seconds()).Emit();

        // Grant partition_access to globaladmin for all org schemas
        await _fixture.DataSource.ExecuteNonQuery(
            "DELETE FROM public.partition_access; " +
            "INSERT INTO public.partition_access (user_id, partition) VALUES " +
            "('globaladmin', 'orgalpha'), ('globaladmin', 'orgbeta')", ct)
            .Should().Within(30.Seconds()).Emit();

        // Cross-schema search for Organization nodes as globaladmin
        var results = await CallSearchAcrossSchemasAsync(
            $"LOWER(n.node_type) = '{SpaceNodeType.NodeType.ToLowerInvariant()}'",
            username, "last_modified DESC", 50, ct)
            .Run().Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(2, "Global admin should see all organizations (has partition_access to both)");
        results.Select(n => n.Name).Should().Contain("OrgAlpha Corp");
        results.Select(n => n.Name).Should().Contain("OrgBeta Corp");
    }

    /// <summary>
    /// The onboarding bootstrap check ("is there a platform admin yet?") must actually SEE the
    /// grant on partitioned PG. The old check — <c>namespace:User limit:1</c> — matched NOTHING
    /// (users are partition roots with namespace <c>''</c>), so every onboarder counted as the
    /// first user: the invitation gate was bypassed and GrantPlatformAdmin fired for everyone
    /// (the 43-root-superuser incident; once AccessAssignmentGuard refused that shape, every
    /// onboarding submit failed instead — memex-cloud, user 'bing', 2026-08-01). The fixed
    /// check is PATH-scoped on Admin/_Access, the same routing the invitation query relies on.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task BootstrapCheck_PathScopedAdminGrantQuery_SeesTheGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "Admin",
            Schema = "admin",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (_, adminAdapter) = await _fixture.CreateSchemaAdapter("admin", partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

        using var provider = new PostgreSqlPartitionStorageProvider(
            _fixture.DataSource,
            _fixture.ConnectionString,
            new PostgreSqlStorageOptions { ConnectionString = _fixture.ConnectionString },
            partitions: null);
        var query = new PostgreSqlPartitionedMeshQuery(
            new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource),
            partitionProvider: provider);

        // The EXACT query string Onboarding.razor uses for onboarding:firstUserCheck.
        const string bootstrapQuery =
            "path:Admin/_Access scope:children nodeType:AccessAssignment limit:1";

        // (a) No grant yet → bootstrap: the first onboarder becomes platform admin.
        var before = await query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(bootstrapQuery, WellKnownUsers.System), _options)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);
        before.Items.Should().BeEmpty(
            "an empty Admin/_Access means platform bootstrap — the first user must be granted admin");

        // (b) Write the guard-compliant platform-admin grant (the shape GrantPlatformAdmin
        //     and GlobalAdminSeed produce) — the check must now see it, so the SECOND
        //     onboarder is NOT treated as first user.
        await adminAdapter.Write(new MeshNode("firstadmin_Access", "Admin/_Access")
        {
            Name = "firstadmin — Admin",
            NodeType = "AccessAssignment",
            State = MeshNodeState.Active,
            MainNode = "Admin",
            Content = new AccessAssignment
            {
                DisplayName = "firstadmin",
                AccessObject = "firstadmin",
                Roles = [new RoleAssignment { Role = Role.Admin.Id }]
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        var after = await query
            .Query<MeshNode>(MeshQueryRequest.FromQuery(bootstrapQuery, WellKnownUsers.System), _options)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);
        after.Items.Should().ContainSingle(
            "the bootstrap check MUST see an existing platform-admin grant — matching nothing " +
            "here is what made every onboarder a 'first user' (gate bypass + root-grant misfire)");
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task PopulateSearchableSchemasAsync(IEnumerable<string> schemas, CancellationToken ct)
    {
        await using (var cmd = _fixture.DataSource.CreateCommand("DELETE FROM public.searchable_schemas"))
            await cmd.ExecuteNonQueryAsync(ct);

        foreach (var schema in schemas)
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                "INSERT INTO public.searchable_schemas (schema_name) VALUES ($1) ON CONFLICT DO NOTHING");
            cmd.Parameters.AddWithValue(schema);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<List<MeshNode>> CallSearchAcrossSchemasAsync(
        string whereClause, string? userId, string orderBy, int limit, CancellationToken ct)
    {
        var results = new List<MeshNode>();
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT * FROM public.search_across_schemas(@p_where, @p_user, @p_order, @p_limit) " +
            "AS t(id TEXT, namespace TEXT, name TEXT, node_type TEXT, category TEXT, icon TEXT, " +
            "display_order INT, last_modified TIMESTAMPTZ, version BIGINT, state SMALLINT, " +
            "content JSONB, desired_id TEXT, main_node TEXT)");
        cmd.Parameters.Add(new NpgsqlParameter("@p_where", string.IsNullOrEmpty(whereClause) ? "" : whereClause));
        cmd.Parameters.Add(new NpgsqlParameter("@p_user", (object?)userId ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("@p_order", orderBy));
        cmd.Parameters.Add(new NpgsqlParameter("@p_limit", limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var ns = reader.IsDBNull(1) ? null : reader.GetString(1);
            results.Add(new MeshNode(id, string.IsNullOrEmpty(ns) ? null : ns)
            {
                Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                NodeType = reader.IsDBNull(3) ? null : reader.GetString(3),
                MainNode = reader.IsDBNull(12) ? id : reader.GetString(12)
            });
        }
        return results;
    }
}
