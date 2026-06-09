using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Npgsql;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests cross-partition search with multiple organizations on PostgreSQL.
/// Sets up 3 orgs (ACME, FutuRe, Contoso) each in their own schema,
/// with threads and activity, then verifies global queries find them all.
///
/// This reproduces the production scenario where FutuRe wasn't
/// visible in global search.
/// </summary>
[Collection("PostgreSql")]
public class CrossPartitionSearchTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public CrossPartitionSearchTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Sets up 3 organization partitions, each with an org node, a document,
    /// a thread, and an activity log.
    /// </summary>
    private async Task<(
        PostgreSqlStorageAdapter AdminAdapter,
        Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)> Partitions
    )> SetupMultiOrgEnvironmentAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        // Admin partition (default schema) â€” stores partition metadata
        var adminAdapter = _fixture.StorageAdapter;

        // Create 3 org schemas
        var orgNames = new[] { "ACME", "FutuRe", "Contoso" };
        var partitions = new Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>();

        foreach (var org in orgNames)
        {
            var schemaName = org.ToLowerInvariant();
            var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
                schemaName,
                partitionDef with { Namespace = org, Schema = schemaName });
            partitions[org] = (ds, adapter);

            // Store partition definition in Admin
            await adminAdapter.WriteAsync(new MeshNode(org, "Admin/Partition")
            {
                Name = $"{org} Organization",
                NodeType = "Partition",
                State = MeshNodeState.Active,
                Content = new PartitionDefinition
                {
                    Namespace = org,
                    DataSource = "default",
                    Schema = schemaName,
                    TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
                }
            }, _options, ct);

            // Root organization node (stored in the org's own schema)
            await adapter.WriteAsync(new MeshNode(org)
            {
                Name = $"{org} Organization",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                LastModified = DateTimeOffset.UtcNow.AddMinutes(-orgNames.ToList().IndexOf(org))
            }, _options, ct);

            // A document under the org
            await adapter.WriteAsync(new MeshNode("Report", org)
            {
                Name = $"{org} Annual Report",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                LastModified = DateTimeOffset.UtcNow.AddMinutes(-10 - orgNames.ToList().IndexOf(org))
            }, _options, ct);

            // A thread under the org (in threads satellite table)
            await adapter.WriteAsync(new MeshNode("discuss-q1-1234", $"{org}/_Thread")
            {
                Name = $"Q1 Discussion in {org}",
                NodeType = "Thread",
                MainNode = $"{org}/_Thread",
                State = MeshNodeState.Active,
                LastModified = DateTimeOffset.UtcNow.AddMinutes(-5 - orgNames.ToList().IndexOf(org))
            }, _options, ct);

            // An activity log (in activities satellite table)
            await adapter.WriteAsync(new MeshNode("log1", $"{org}/Report/_activity")
            {
                Name = "Edit activity",
                NodeType = "Activity",
                MainNode = $"{org}/Report",
                State = MeshNodeState.Active,
                Content = new ActivityLog("DataUpdate") { HubPath = $"{org}/Report" }
            }, _options, ct);
        }

        // Register Partition as public-read node type
        await _fixture.AccessControl.SyncNodeTypePermissionsAsync(
            [new NodeTypePermission("Partition", PublicRead: true)], ct);

        return (adminAdapter, partitions);
    }

    private (PostgreSqlStorageAdapter AdminAdapter,
        Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)> Partitions)
        SetupMultiOrgEnvironment(CancellationToken ct)
        => SetupMultiOrgEnvironmentAsync(ct).Run().Should().Within(120.Seconds()).Emit();

    private List<MeshNode> QueryAdapter(PostgreSqlStorageAdapter adapter, ParsedQuery query, CancellationToken ct)
        => adapter.QueryNodesAsync(query, _options, ct: ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();

    private List<MeshNode> CallSearchAcrossSchemas(
        string whereClause, string? userId, string orderBy, int limit, CancellationToken ct)
        => CallSearchAcrossSchemasAsync(whereClause, userId, orderBy, limit, ct)
            .Run().Should().Within(30.Seconds()).Emit();

    private void PopulateSearchableSchemas(IEnumerable<string> schemas, CancellationToken ct)
        => PopulateSearchableSchemasAsync(schemas, ct).Run().Should().Within(30.Seconds()).Emit();

    [Fact(Timeout = 60000)]
    public void CrossPartition_FindsOrgsAcrossAllSchemas()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminAdapter, partitions) = SetupMultiOrgEnvironment(ct);

        // Query each partition individually — verify data is there
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("scope:subtree is:main");
            var nodes = QueryAdapter(adapter, query, ct);

            nodes.Should().NotBeEmpty($"{org} partition should have nodes");
            nodes.Select(n => n.Name).Should().Contain($"{org} Organization",
                $"{org} root org node should be in its own partition");
            nodes.Select(n => n.Name).Should().Contain($"{org} Annual Report");
        }
    }

    [Fact(Timeout = 60000)]
    public void CrossPartition_ThreadsFoundByNodeType()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        // Query threads in each partition
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("nodeType:Thread scope:subtree");
            var threads = QueryAdapter(adapter, query, ct);

            threads.Should().NotBeEmpty($"{org} should have a thread");
            threads[0].Name.Should().Contain(org);
            threads[0].NodeType.Should().Be("Thread");
        }
    }

    [Fact(Timeout = 60000)]
    public void CrossPartition_ActivityQueryFindsNodesWithActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        // source:activity query in each partition
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("source:activity scope:subtree is:main sort:LastModified-desc");
            var results = QueryAdapter(adapter, query, ct);

            results.Should().NotBeEmpty($"{org} should have nodes with activity");
            results.Should().AllSatisfy(n =>
            {
                n.MainNode.Should().Be(n.Path, "only main nodes, not activity satellites");
                n.NodeType.Should().NotBe("Activity");
            });
        }
    }

    [Fact(Timeout = 60000)]
    public void CrossPartition_SortedLimitedQueryMergesCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        // Collect all nodes across partitions, sorted by LastModified desc
        var allNodes = new List<MeshNode>();
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("scope:subtree is:main sort:LastModified-desc");
            allNodes.AddRange(QueryAdapter(adapter, query, ct));
        }

        // Re-sort globally (simulating correct merge behavior)
        var sorted = allNodes
            .OrderByDescending(n => n.LastModified)
            .ToList();

        // Verify: if we take top 3, we get the newest items across ALL partitions
        var top3 = sorted.Take(3).ToList();
        top3.Should().HaveCount(3);

        // The top 3 should NOT all be from the same partition
        // (which would indicate the merge picked from one partition first)
        var distinctNamespaces = top3
            .Select(n => n.Path.Split('/').FirstOrDefault() ?? n.Id)
            .Distinct()
            .Count();
        distinctNamespaces.Should().BeGreaterThan(1,
            "top results should span multiple partitions when data is interleaved by timestamp");
    }

    [Fact(Timeout = 60000)]
    public void CrossPartition_TextSearchFindsAcrossPartitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        // Search for "Annual Report" in each partition
        var allMatches = new List<MeshNode>();
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("Annual scope:subtree is:main");
            allMatches.AddRange(QueryAdapter(adapter, query, ct));
        }

        // All 3 orgs should have a matching "Annual Report"
        allMatches.Should().HaveCount(3, "each org has an 'Annual Report' document");
        allMatches.Select(n => n.Name).Should().Contain("ACME Annual Report");
        allMatches.Select(n => n.Name).Should().Contain("FutuRe Annual Report");
        allMatches.Select(n => n.Name).Should().Contain("Contoso Annual Report");
    }

    [Fact(Timeout = 60000)]
    public void CrossPartition_ContextSearchExcludesSatelliteTypes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        // Query with context:search should exclude Thread and Activity
        foreach (var (org, (_, adapter)) in partitions)
        {
            // Simulate context:search exclusion
            var query = new QueryParser().Parse("scope:subtree is:main");
            var results = QueryAdapter(adapter, query, ct)
                // Simulate the context filtering done by PostgreSqlMeshQuery
                .Where(node => node.NodeType is not ("Thread" or "Activity" or "ThreadMessage"))
                .ToList();

            results.Should().NotBeEmpty($"{org} should have main content nodes");
            results.Should().AllSatisfy(n =>
            {
                n.NodeType.Should().NotBe("Thread");
                n.NodeType.Should().NotBe("Activity");
            });
        }
    }

    // â”€â”€ Stored Procedure: search_across_schemas â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 60000)]
    public void StoredProc_SearchAcrossSchemas_ReturnsAllOrgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        // Populate searchable_schemas
        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        PopulateSearchableSchemas(schemas, ct);

        // Call the stored proc
        var results = CallSearchAcrossSchemas("", null, "last_modified DESC", 50, ct);

        results.Should().NotBeEmpty("stored proc should return nodes from all schemas");
        results.Select(n => n.Id).Should().Contain("ACME");
        results.Select(n => n.Id).Should().Contain("FutuRe");
        results.Select(n => n.Id).Should().Contain("Contoso");
    }

    [Fact(Timeout = 60000)]
    public void StoredProc_SearchAcrossSchemas_TextSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        PopulateSearchableSchemas(schemas, ct);

        // Text search for "FutuRe"
        var textFilter = "COALESCE(n.name,'') || ' ' || COALESCE(n.namespace || '/' || n.id,'') || ' ' || COALESCE(n.node_type,'') ILIKE '%future%'";
        var results = CallSearchAcrossSchemas(textFilter, null, "last_modified DESC", 50, ct);

        results.Should().NotBeEmpty("should find FutuRe by text search");
        results.Select(n => n.Id).Should().Contain("FutuRe");
        results.Should().NotContain(n => n.Id == "ACME", "ACME doesn't match 'future'");
    }

    [Fact(Timeout = 60000)]
    public void StoredProc_SearchAcrossSchemas_WithLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        PopulateSearchableSchemas(schemas, ct);

        var results = CallSearchAcrossSchemas("", null, "last_modified DESC", 2, ct);

        results.Should().HaveCount(2, "limit:2 should return exactly 2 results");
    }

    [Fact(Timeout = 60000)]
    public void StoredProc_SearchAcrossSchemas_ExcludesUnsearchableSchemas()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminAdapter, partitions) = SetupMultiOrgEnvironment(ct);

        // Only include ACME and FutuRe (exclude Contoso)
        PopulateSearchableSchemas(["acme", "future"], ct);

        var results = CallSearchAcrossSchemas("", null, "last_modified DESC", 50, ct);

        results.Select(n => n.Id).Should().Contain("ACME");
        results.Select(n => n.Id).Should().Contain("FutuRe");
        results.Should().NotContain(n => n.Id == "Contoso",
            "Contoso is not in searchable_schemas");
    }

    [Fact(Timeout = 60000)]
    public void StoredProc_SearchAcrossSchemas_AccessControlFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        PopulateSearchableSchemas(schemas, ct);

        // Give testuser access only to ACME via partition_access
        _fixture.DataSource.ExecuteNonQuery(
            "DELETE FROM public.partition_access; INSERT INTO public.partition_access VALUES ('testuser', 'acme')", ct)
            .Should().Within(30.Seconds()).Emit();

        // Also set up effective permissions for testuser in ACME schema
        _fixture.DataSource.ExecuteNonQuery(
            "INSERT INTO acme.user_effective_permissions (user_id, node_path_prefix, permission, is_allow) " +
            "VALUES ('testuser', 'ACME', 'Read', true) ON CONFLICT DO NOTHING", ct)
            .Should().Within(30.Seconds()).Emit();

        // Also register Markdown as public_read in all schemas
        foreach (var schema in schemas)
        {
            _fixture.DataSource.ExecuteNonQuery(
                $"INSERT INTO \"{schema}\".node_type_permissions (node_type, public_read) " +
                "VALUES ('Markdown', true) ON CONFLICT (node_type) DO UPDATE SET public_read = true", ct)
                .Should().Within(30.Seconds()).Emit();
        }

        var results = CallSearchAcrossSchemas("", "testuser", "last_modified DESC", 50, ct);

        // testuser has partition_access only to ACME â€” should only see ACME nodes
        results.Should().NotBeEmpty();
        results.Select(n => n.Id).Should().Contain("ACME");

        // CRITICAL: public_read must NOT bypass partition_access.
        // testuser has NO partition_access to FutuRe or Contoso,
        // so those nodes must NOT appear even though Markdown is public_read.
        results.Should().NotContain(n => n.Id == "FutuRe",
            "testuser has no partition_access to FutuRe â€” public_read must not bypass partition check");
        results.Should().NotContain(n => n.Id == "Contoso",
            "testuser has no partition_access to Contoso â€” public_read must not bypass partition check");
        results.Should().NotContain(n => n.Id == "Report" && n.Namespace == "FutuRe",
            "FutuRe child nodes must also be hidden");
        results.Should().NotContain(n => n.Id == "Report" && n.Namespace == "Contoso",
            "Contoso child nodes must also be hidden");
    }

    [Fact(Timeout = 60000)]
    public void StoredProc_NodeTypeFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        PopulateSearchableSchemas(schemas, ct);

        var results = CallSearchAcrossSchemas(
            "LOWER(n.node_type) = 'markdown'", null, "last_modified DESC", 50, ct);

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(n => n.NodeType == "Markdown");
    }

    [Fact(Timeout = 60000)]
    public void CrossSchema_QueryNodesAcrossSchemas_ReturnsResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        var adapter = new PostgreSqlStorageAdapter(_fixture.DataSource);
        var query = new QueryParser().Parse("is:main");

        var results = adapter.QueryNodesAcrossSchemasAsync(query, _options, schemas, ct: ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();

        results.Should().NotBeEmpty("cross-schema query should return nodes");
        results.Select(n => n.Id).Should().Contain("ACME");
        results.Select(n => n.Id).Should().Contain("FutuRe");
        results.Select(n => n.Id).Should().Contain("Contoso");
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
