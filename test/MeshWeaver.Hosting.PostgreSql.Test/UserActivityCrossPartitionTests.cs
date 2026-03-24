using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that UserActivity dashboard queries return results across partitions.
/// Covers: Latest Threads, Activity Feed, Recently Viewed — all of which use
/// satellite tables (threads, activities, user_activities) that require
/// per-partition fan-out instead of the cross-schema stored proc.
/// </summary>
[Collection("PostgreSql")]
public class UserActivityCrossPartitionTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public UserActivityCrossPartitionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>>
        SetupMultiOrgWithThreadsAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var orgNames = new[] { "OrgA", "OrgB" };
        var partitions = new Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>();

        foreach (var org in orgNames)
        {
            var schemaName = org.ToLowerInvariant();
            var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
                schemaName,
                partitionDef with { Namespace = org, Schema = schemaName },
                ct);
            partitions[org] = (ds, adapter);

            // Root org node
            await adapter.WriteAsync(new MeshNode(org)
            {
                Name = $"{org} Corp",
                NodeType = "Organization",
                State = MeshNodeState.Active,
            }, _options, ct);

            // A document under the org
            await adapter.WriteAsync(new MeshNode("Doc1", org)
            {
                Name = $"{org} Document",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            }, _options, ct);

            // Thread in the threads satellite table (namespace has _Thread segment)
            await adapter.WriteAsync(new MeshNode("thread-1", $"{org}/_Thread")
            {
                Name = $"Discussion in {org}",
                NodeType = "Thread",
                MainNode = $"{org}/_Thread",
                State = MeshNodeState.Active,
                Content = new { CreatedBy = "testuser" }
            }, _options, ct);

            // Activity log in the activities satellite table
            await adapter.WriteAsync(new MeshNode("act-1", $"{org}/Doc1/_activity")
            {
                Name = "DataUpdate",
                NodeType = "Activity",
                MainNode = $"{org}/Doc1",
                State = MeshNodeState.Active,
                Content = new ActivityLog("DataUpdate") { HubPath = $"{org}/Doc1" }
            }, _options, ct);

            // Grant testuser access
            var ac = new PostgreSqlAccessControl(ds);
            await ac.GrantAsync(org, "testuser", "Read", isAllow: true, ct);
            await ac.SyncNodeTypePermissionsAsync(
                [new NodeTypePermission("Organization", PublicRead: true),
                 new NodeTypePermission("Markdown", PublicRead: true)], ct);
        }

        await PopulateSearchableSchemasAsync(orgNames.Select(o => o.ToLowerInvariant()), ct);

        return partitions;
    }

    [Fact(Timeout = 60000)]
    public async Task LatestThreads_FoundInEachPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupMultiOrgWithThreadsAsync(ct);

        // The Latest Threads query: nodeType:Thread content.CreatedBy:testuser scope:descendants
        // This should find threads in satellite tables per partition
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse(
                $"nodeType:Thread scope:descendants");
            var results = new List<MeshNode>();
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
                results.Add(node);

            results.Should().NotBeEmpty($"{org} should have threads in satellite table");
            results.Should().Contain(n => n.NodeType == "Thread",
                $"{org} thread should be found via per-partition query");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task LatestThreads_FilterByCreatedBy()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupMultiOrgWithThreadsAsync(ct);

        // Verify content.CreatedBy filter works per partition
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse(
                "nodeType:Thread content.createdBy:testuser scope:descendants");
            var results = new List<MeshNode>();
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
                results.Add(node);

            results.Should().NotBeEmpty($"{org} should find threads by testuser");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task ActivityFeed_FoundInEachPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupMultiOrgWithThreadsAsync(ct);

        // The Activity Feed query: source:activity scope:subtree is:main
        // source:activity JOINs with activities satellite table
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse(
                "source:activity scope:subtree is:main sort:LastModified-desc");
            var results = new List<MeshNode>();
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
                results.Add(node);

            results.Should().NotBeEmpty($"{org} should have nodes with activity");
            results.Should().OnlyContain(n => n.MainNode == n.Path,
                "source:activity returns main content nodes, not activity satellites");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Threads_NotInCrossSchemaStoredProc()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupMultiOrgWithThreadsAsync(ct);

        // Cross-schema stored proc only searches mesh_nodes with main_node = path.
        // Threads are in satellite tables with main_node != path, so they should NOT appear.
        var results = await CallSearchAcrossSchemasAsync(
            "LOWER(n.node_type) = 'thread'", "testuser", "last_modified DESC", 50, ct);

        results.Should().BeEmpty(
            "Threads are in satellite tables — stored proc only searches mesh_nodes");
    }

    [Fact(Timeout = 60000)]
    public async Task MainNodes_VisibleInCrossSchemaStoredProc()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupMultiOrgWithThreadsAsync(ct);

        // Main content nodes (Organization, Markdown) should be found
        var results = await CallSearchAcrossSchemasAsync(
            "", "testuser", "last_modified DESC", 50, ct);

        results.Should().NotBeEmpty("Main nodes should be visible");
        results.Select(n => n.Id).Should().Contain("OrgA");
        results.Select(n => n.Id).Should().Contain("OrgB");
        results.Select(n => n.Id).Should().Contain("Doc1");
        results.Should().NotContain(n => n.NodeType == "Thread",
            "Threads should not appear in cross-schema results");
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
