using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Memex.Portal.Shared;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that the first user onboarding flow creates a global admin.
/// The first user should receive Admin role at Admin/_Access (stored in admin.access table),
/// giving them full permissions across all partitions.
///
/// Bug: Onboarding.razor was calling AddUserRoleAsync(username, "PlatformAdmin", "Admin", username)
/// — correct namespace but wrong role ("PlatformAdmin" instead of "Admin").
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
        await _fixture.CleanDataAsync();

        // Create admin schema (global admin assignments live here)
        var partitionDef = new PartitionDefinition
        {
            Namespace = "Admin",
            Schema = "admin",
            TableMappings = PartitionDefinition.StandardTableMappings
        };
        var (adminDs, adminAdapter) = await _fixture.CreateSchemaAdapterAsync(
            "admin", partitionDef, ct);

        // Create User schema
        var (_, userAdapter) = await _fixture.CreateSchemaAdapterAsync("user", null, ct);

        // Step 1: Create User node (simulates NodeFactory.CreateNodeAsync in onboarding)
        const string username = "firstadmin";
        await userAdapter.WriteAsync(new MeshNode(username, "User")
        {
            Name = "First Admin",
            NodeType = "User",
            State = MeshNodeState.Active,
            Content = new User
            {
                FullName = "First Admin",
                Email = "admin@example.com"
            }
        }, _options, ct);

        // Step 2: Assign global Admin role (simulates the fixed onboarding code)
        // AddUserRoleAsync(username, Role.Admin.Id, "Admin", username)
        // → namespace = "Admin/_Access", stored in admin.access table
        var ns = "Admin/_Access";
        var nodeId = $"{username}_Access";
        await adminAdapter.WriteAsync(new MeshNode(nodeId, ns)
        {
            Name = username,
            NodeType = "AccessAssignment",
            State = MeshNodeState.Active,
            MainNode = ns,
            Content = new AccessAssignment
            {
                DisplayName = username,
                AccessObject = username,
                Roles = [new RoleAssignment { Role = Role.Admin.Id }]
            }
        }, _options, ct);

        // Step 3: Rebuild permissions (normally triggered by DB trigger)
        var adminAccessControl = new PostgreSqlAccessControl(adminDs);
        await adminAccessControl.RebuildDenormalizedTableAsync(ct);

        // Verify: user has all permissions at Admin/_Access scope
        var allPermissions = new[] { "Read", "Create", "Update", "Delete", "Comment", "Execute", "Thread" };
        foreach (var perm in allPermissions)
        {
            var hasPermission = await adminAccessControl.HasPermissionAsync(username, "Admin/_Access", perm, ct);
            hasPermission.Should().BeTrue($"Global admin should have {perm} permission at Admin/_Access scope");
        }

        // Verify: partition_access entry exists for admin partition
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT partition FROM public.partition_access WHERE user_id = $1 ORDER BY partition");
        cmd.Parameters.AddWithValue(username);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var partitions = new List<string>();
        while (await reader.ReadAsync(ct))
            partitions.Add(reader.GetString(0));

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
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        // Create admin schema with global admin
        var (adminDs, adminAdapter) = await _fixture.CreateSchemaAdapterAsync(
            "admin",
            partitionDef with { Namespace = "Admin", Schema = "admin" },
            ct);

        const string username = "globaladmin";
        await adminAdapter.WriteAsync(new MeshNode($"{username}_Access", "Admin/_Access")
        {
            Name = username,
            NodeType = "AccessAssignment",
            State = MeshNodeState.Active,
            MainNode = "Admin/_Access",
            Content = new AccessAssignment
            {
                DisplayName = username,
                AccessObject = username,
                Roles = [new RoleAssignment { Role = Role.Admin.Id }]
            }
        }, _options, ct);

        var adminAccessControl = new PostgreSqlAccessControl(adminDs);
        await adminAccessControl.RebuildDenormalizedTableAsync(ct);

        // Create 2 org schemas with Organization nodes
        var orgNames = new[] { "OrgAlpha", "OrgBeta" };
        foreach (var org in orgNames)
        {
            var schemaName = org.ToLowerInvariant();
            var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
                schemaName, partitionDef with { Namespace = org, Schema = schemaName }, ct);

            await adapter.WriteAsync(new MeshNode(org)
            {
                Name = $"{org} Corp",
                NodeType = OrganizationNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new Organization { Name = $"{org} Corp" }
            }, _options, ct);

            // Register Organization as public_read in each org schema
            var ac = new PostgreSqlAccessControl(ds);
            await ac.SyncNodeTypePermissionsAsync(
                [new NodeTypePermission(OrganizationNodeType.NodeType, PublicRead: true)], ct);
        }

        // Populate searchable_schemas
        await PopulateSearchableSchemasAsync(orgNames.Select(o => o.ToLowerInvariant()), ct);

        // Grant partition_access to globaladmin for all org schemas
        await using (var cmd = _fixture.DataSource.CreateCommand(
            "DELETE FROM public.partition_access; " +
            "INSERT INTO public.partition_access (user_id, partition) VALUES " +
            "('globaladmin', 'orgalpha'), ('globaladmin', 'orgbeta')"))
            await cmd.ExecuteNonQueryAsync(ct);

        // Cross-schema search for Organization nodes as globaladmin
        var results = await CallSearchAcrossSchemasAsync(
            $"LOWER(n.node_type) = '{OrganizationNodeType.NodeType.ToLowerInvariant()}'",
            username, "last_modified DESC", 50, ct);

        results.Should().HaveCount(2, "Global admin should see all organizations (has partition_access to both)");
        results.Select(n => n.Name).Should().Contain("OrgAlpha Corp");
        results.Select(n => n.Name).Should().Contain("OrgBeta Corp");
    }

    // ── Helpers ──────────────────────────────────────────────────────

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
