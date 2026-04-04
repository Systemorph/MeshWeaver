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
/// Tests that a Global Admin can see all Organization nodes via cross-schema search.
/// Reproduces the scenario: admin navigates to Organization/Search, sees all orgs.
/// The key query is nodeType:Organization (not namespace:Organization) because
/// Organization instances live at root paths, not under the "Organization" namespace.
/// </summary>
[Collection("PostgreSql")]
public class GlobalAdminOrganizationSearchTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public GlobalAdminOrganizationSearchTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>>
        SetupOrganizationsAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var orgNames = new[] { "AlphaOrg", "BetaOrg", "GammaOrg" };
        var partitions = new Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>();

        foreach (var org in orgNames)
        {
            var schemaName = org.ToLowerInvariant();
            var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
                schemaName,
                partitionDef with { Namespace = org, Schema = schemaName },
                ct);
            partitions[org] = (ds, adapter);

            // Organization root node in its own schema
            await adapter.WriteAsync(new MeshNode(org)
            {
                Name = $"{org} Inc.",
                NodeType = OrganizationNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new Organization { Name = $"{org} Inc." }
            }, _options, ct);

            // A child Markdown node under the org
            await adapter.WriteAsync(new MeshNode("Readme", org)
            {
                Name = $"{org} Readme",
                NodeType = "Markdown",
                State = MeshNodeState.Active
            }, _options, ct);

            // Register Organization as public_read in each schema
            var ac = new PostgreSqlAccessControl(ds);
            await ac.SyncNodeTypePermissionsAsync(
                [new NodeTypePermission(OrganizationNodeType.NodeType, PublicRead: true)], ct);
        }

        // Populate searchable_schemas
        await PopulateSearchableSchemasAsync(
            orgNames.Select(o => o.ToLowerInvariant()), ct);

        return partitions;
    }

    [Fact(Timeout = 60000)]
    public async Task GlobalAdmin_SeesAllOrganizations_ViaCrossSchemaSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupOrganizationsAsync(ct);

        // Global Admin: has partition_access to ALL org partitions
        const string adminUserId = "globaladmin";
        await using (var cmd = _fixture.DataSource.CreateCommand(
            "DELETE FROM public.partition_access; " +
            "INSERT INTO public.partition_access (user_id, partition) VALUES " +
            "('globaladmin', 'alphaorg'), ('globaladmin', 'betaorg'), ('globaladmin', 'gammaorg')"))
            await cmd.ExecuteNonQueryAsync(ct);

        // Query: nodeType:Organization — the fixed query for Organization Search
        var nodeTypeFilter = $"LOWER(n.node_type) = '{OrganizationNodeType.NodeType.ToLowerInvariant()}'";
        var results = await CallSearchAcrossSchemasAsync(
            nodeTypeFilter, adminUserId, "last_modified DESC", 50, ct);

        results.Should().HaveCount(3, "Global Admin should see all 3 organizations");
        results.Select(n => n.Name).Should().Contain("AlphaOrg Inc.");
        results.Select(n => n.Name).Should().Contain("BetaOrg Inc.");
        results.Select(n => n.Name).Should().Contain("GammaOrg Inc.");
        results.Should().OnlyContain(n => n.NodeType == OrganizationNodeType.NodeType);
    }

    [Fact(Timeout = 60000)]
    public async Task PublicRead_StillRequiresPartitionAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetupOrganizationsAsync(ct);

        // Regular user with NO partition_access — even public_read must not bypass partition check
        await using (var cmd = _fixture.DataSource.CreateCommand("DELETE FROM public.partition_access"))
            await cmd.ExecuteNonQueryAsync(ct);

        var nodeTypeFilter = $"LOWER(n.node_type) = '{OrganizationNodeType.NodeType.ToLowerInvariant()}'";
        var results = await CallSearchAcrossSchemasAsync(
            nodeTypeFilter, "regularuser", "last_modified DESC", 50, ct);

        results.Should().BeEmpty(
            "public_read must NOT bypass partition_access — user without partition access sees nothing");
    }

    [Fact(Timeout = 60000)]
    public async Task PublicRead_SkipsNodeLevelChecks_WithinAccessiblePartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await SetupOrganizationsAsync(ct);

        // Give user partition_access to AlphaOrg only (no node-level permissions)
        await using (var cmd = _fixture.DataSource.CreateCommand(
            "DELETE FROM public.partition_access; " +
            "INSERT INTO public.partition_access (user_id, partition) VALUES ('regularuser', 'alphaorg')"))
            await cmd.ExecuteNonQueryAsync(ct);

        // Search for all nodes — public_read types visible without node-level perms, but only in alphaorg
        var results = await CallSearchAcrossSchemasAsync(
            "", "regularuser", "last_modified DESC", 50, ct);

        // Should see Organization nodes from AlphaOrg (public_read + partition_access)
        results.Should().Contain(n => n.Id == "AlphaOrg",
            "AlphaOrg Organization should be visible (public_read + partition_access)");
        // Should NOT see BetaOrg or GammaOrg (no partition_access)
        results.Should().NotContain(n => n.Id == "BetaOrg",
            "BetaOrg should be hidden — no partition_access");
        results.Should().NotContain(n => n.Id == "GammaOrg",
            "GammaOrg should be hidden — no partition_access");
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
