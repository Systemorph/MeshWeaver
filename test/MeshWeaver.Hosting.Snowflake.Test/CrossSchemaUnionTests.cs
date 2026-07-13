using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Tests cross-partition search with multiple organizations on Snowflake — the port of the PG
/// test project's <c>CrossPartitionSearchTests</c>. Sets up 3 orgs (ACME, FutuRe, Contoso) each
/// in their own schema, with threads and activity, then verifies global queries find them all.
///
/// This reproduces the production scenario where FutuRe wasn't
/// visible in global search.
///
/// <para><b>Adaptation from PG</b>: PG's <c>StoredProc_SearchAcrossSchemas_*</c> facts exercise
/// <c>SELECT * FROM public.search_across_schemas(...)</c>. Snowflake has NO stored procedure —
/// the same UNION is generated in C# — so those scenarios run through
/// <see cref="SnowflakeCrossSchemaQueryProvider.QueryAcrossSchemasAsync"/> (renamed
/// <c>CrossSchema_SearchAcrossSchemas_*</c>), with the registry populated in
/// <c>public.searchable_schemas</c> exactly like PG and read back via
/// <see cref="SnowflakeCrossSchemaQueryProvider.GetSearchableSchemasAsync"/>. The proc's
/// implicit <c>main_node = path</c> / <c>last_modified DESC</c> / <c>LIMIT 50</c> defaults are
/// reproduced by the provider itself. The PG twin's <c>SearchableSchemasSyncThrottleTests</c>
/// scenarios are folded in at the bottom (same throttle, same <c>ActualSyncCount</c> hook).</para>
/// </summary>
[Collection("Snowflake")]
public class CrossSchemaUnionTests
{
    private readonly SnowflakeFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public CrossSchemaUnionTests(SnowflakeFixture fixture)
    {
        _fixture = fixture;
    }

    private string SearchableSchemasTable
        => SnowflakeIdentifiers.Qualify(_fixture.Options.Schema, "searchable_schemas");

    private string PartitionAccessTable
        => SnowflakeIdentifiers.Qualify(_fixture.Options.Schema, "partition_access");

    private SnowflakeCrossSchemaQueryProvider CreateProvider()
        => new(_fixture.ConnectionSource, options: _fixture.Options);

    // The Snowflake adapter's write surface is the reactive Write (no public WriteAsync like
    // PG) — bridged to Task with the tests-only Rx→Task bridge for the async setup leaf.
    private Task WriteAsync(SnowflakeStorageAdapter adapter, MeshNode node, CancellationToken ct)
        => adapter.Write(node, _options).FirstAsync().ToTask(ct);

    /// <summary>
    /// Sets up 3 organization partitions, each with an org node, a document,
    /// a thread, and an activity log.
    /// </summary>
    private async Task<(
        SnowflakeStorageAdapter AdminAdapter,
        Dictionary<string, (SnowflakeConnectionSource Source, SnowflakeStorageAdapter Adapter)> Partitions
    )> SetupMultiOrgEnvironmentAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        // Admin partition (default schema) — stores partition metadata
        var adminAdapter = _fixture.StorageAdapter;

        // Create 3 org schemas
        var orgNames = new[] { "ACME", "FutuRe", "Contoso" };
        var partitions = new Dictionary<string, (SnowflakeConnectionSource Source, SnowflakeStorageAdapter Adapter)>();

        foreach (var org in orgNames)
        {
            var schemaName = org.ToLowerInvariant();
            var (source, adapter) = await _fixture.CreateSchemaAdapterAsync(
                schemaName,
                partitionDef with { Namespace = org, Schema = schemaName }, ct);
            partitions[org] = (source, adapter);

            // Store partition definition in Admin
            await WriteAsync(adminAdapter, new MeshNode(org, "Admin/Partition")
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
            }, ct);

            // Root organization node (stored in the org's own schema)
            await WriteAsync(adapter, new MeshNode(org)
            {
                Name = $"{org} Organization",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                LastModified = DateTimeOffset.UtcNow.AddMinutes(-orgNames.ToList().IndexOf(org))
            }, ct);

            // A document under the org
            await WriteAsync(adapter, new MeshNode("Report", org)
            {
                Name = $"{org} Annual Report",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                LastModified = DateTimeOffset.UtcNow.AddMinutes(-10 - orgNames.ToList().IndexOf(org))
            }, ct);

            // A thread under the org (in threads satellite table)
            await WriteAsync(adapter, new MeshNode("discuss-q1-1234", $"{org}/_Thread")
            {
                Name = $"Q1 Discussion in {org}",
                NodeType = "Thread",
                MainNode = $"{org}/_Thread",
                State = MeshNodeState.Active,
                LastModified = DateTimeOffset.UtcNow.AddMinutes(-5 - orgNames.ToList().IndexOf(org))
            }, ct);

            // An activity log (in activities satellite table)
            await WriteAsync(adapter, new MeshNode("log1", $"{org}/Report/_activity")
            {
                Name = "Edit activity",
                NodeType = "Activity",
                MainNode = $"{org}/Report",
                State = MeshNodeState.Active,
                Content = new ActivityLog("DataUpdate") { HubPath = $"{org}/Report" }
            }, ct);
        }

        // Register Partition as public-read node type
        await _fixture.AccessControl.SyncNodeTypePermissionsAsync(
            [new NodeTypePermission("Partition", PublicRead: true)], ct);

        return (adminAdapter, partitions);
    }

    private Task<(SnowflakeStorageAdapter AdminAdapter,
        Dictionary<string, (SnowflakeConnectionSource Source, SnowflakeStorageAdapter Adapter)> Partitions)>
        SetupMultiOrgEnvironment(CancellationToken ct)
        => SetupMultiOrgEnvironmentAsync(ct).Run().Should().Within(120.Seconds()).Emit();

    private Task<List<MeshNode>> QueryAdapter(SnowflakeStorageAdapter adapter, ParsedQuery query, CancellationToken ct)
        => adapter.QueryNodesAsync(query, _options, ct: ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();

    // The provider replacement for PG's CallSearchAcrossSchemas: schemas come from the
    // searchable_schemas registry (like the proc re-reading it) and the proc's orderBy/limit
    // defaults are reproduced inside QueryAcrossSchemasAsync.
    private Task<List<MeshNode>> SearchAcrossSchemas(
        ParsedQuery query, string? userId, CancellationToken ct)
        => SearchAcrossSchemasAsync(query, userId, ct)
            .Run().Should().Within(30.Seconds()).Emit();

    private Task PopulateSearchableSchemas(IEnumerable<string> schemas, CancellationToken ct)
        => PopulateSearchableSchemasAsync(schemas, ct).Run().Should().Within(30.Seconds()).Emit();

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_FindsOrgsAcrossAllSchemas()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (adminAdapter, partitions) = await SetupMultiOrgEnvironment(ct);

        // Query each partition individually — verify data is there
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("scope:subtree is:main");
            var nodes = await QueryAdapter(adapter, query, ct);

            nodes.Should().NotBeEmpty($"{org} partition should have nodes");
            nodes.Select(n => n.Name).Should().Contain($"{org} Organization",
                $"{org} root org node should be in its own partition");
            nodes.Select(n => n.Name).Should().Contain($"{org} Annual Report");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_ThreadsFoundByNodeType()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        // Query threads in each partition
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("nodeType:Thread scope:subtree");
            var threads = await QueryAdapter(adapter, query, ct);

            threads.Should().NotBeEmpty($"{org} should have a thread");
            threads[0].Name.Should().Contain(org);
            threads[0].NodeType.Should().Be("Thread");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_ActivityQueryFindsNodesWithActivity()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        // source:activity query in each partition
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("source:activity scope:subtree is:main sort:LastModified-desc");
            var results = await QueryAdapter(adapter, query, ct);

            results.Should().NotBeEmpty($"{org} should have nodes with activity");
            results.Should().AllSatisfy(n =>
            {
                n.MainNode.Should().Be(n.Path, "only main nodes, not activity satellites");
                n.NodeType.Should().NotBe("Activity");
            });
        }
    }

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_SortedLimitedQueryMergesCorrectly()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        // Collect all nodes across partitions, sorted by LastModified desc
        var allNodes = new List<MeshNode>();
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("scope:subtree is:main sort:LastModified-desc");
            allNodes.AddRange(await QueryAdapter(adapter, query, ct));
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
    public async Task CrossPartition_TextSearchFindsAcrossPartitions()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        // Search for "Annual Report" in each partition
        var allMatches = new List<MeshNode>();
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("Annual scope:subtree is:main");
            allMatches.AddRange(await QueryAdapter(adapter, query, ct));
        }

        // All 3 orgs should have a matching "Annual Report"
        allMatches.Should().HaveCount(3, "each org has an 'Annual Report' document");
        allMatches.Select(n => n.Name).Should().Contain("ACME Annual Report");
        allMatches.Select(n => n.Name).Should().Contain("FutuRe Annual Report");
        allMatches.Select(n => n.Name).Should().Contain("Contoso Annual Report");
    }

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_ContextSearchExcludesSatelliteTypes()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        // Query with context:search should exclude Thread and Activity
        foreach (var (org, (_, adapter)) in partitions)
        {
            // Simulate context:search exclusion
            var query = new QueryParser().Parse("scope:subtree is:main");
            var results = (await QueryAdapter(adapter, query, ct))
                // Simulate the context filtering done by SnowflakeMeshQuery
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

    // ── Cross-schema UNION: QueryAcrossSchemasAsync (PG: the search_across_schemas proc) ──

    [Fact(Timeout = 60000)]
    public async Task CrossSchema_SearchAcrossSchemas_ReturnsAllOrgs()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        // Populate searchable_schemas
        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        await PopulateSearchableSchemas(schemas, ct);

        // Empty filter (PG passed whereClause '') — provider applies the proc's defaults.
        var results = await SearchAcrossSchemas(ParsedQuery.Empty, null, ct);

        results.Should().NotBeEmpty("the cross-schema UNION should return nodes from all schemas");
        results.Select(n => n.Id).Should().Contain("ACME");
        results.Select(n => n.Id).Should().Contain("FutuRe");
        results.Select(n => n.Id).Should().Contain("Contoso");
    }

    [Fact(Timeout = 60000)]
    public async Task CrossSchema_SearchAcrossSchemas_TextSearch()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        await PopulateSearchableSchemas(schemas, ct);

        // Text search for "FutuRe" — PG inlined the ILIKE '%future%' filter into p_where_clause;
        // here the generator produces the equivalent ILIKE clause from the parsed TextSearch.
        var results = await SearchAcrossSchemas(ParsedQuery.Empty with { TextSearch = "future" }, null, ct);

        results.Should().NotBeEmpty("should find FutuRe by text search");
        results.Select(n => n.Id).Should().Contain("FutuRe");
        results.Should().NotContain(n => n.Id == "ACME", "ACME doesn't match 'future'");
    }

    [Fact(Timeout = 60000)]
    public async Task CrossSchema_SearchAcrossSchemas_WithLimit()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        await PopulateSearchableSchemas(schemas, ct);

        var results = await SearchAcrossSchemas(ParsedQuery.Empty with { Limit = 2 }, null, ct);

        results.Should().HaveCount(2, "limit:2 should return exactly 2 results");
    }

    [Fact(Timeout = 60000)]
    public async Task CrossSchema_SearchAcrossSchemas_ExcludesUnsearchableSchemas()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (adminAdapter, partitions) = await SetupMultiOrgEnvironment(ct);

        // Only include ACME and FutuRe (exclude Contoso)
        await PopulateSearchableSchemas(["acme", "future"], ct);

        var results = await SearchAcrossSchemas(ParsedQuery.Empty, null, ct);

        results.Select(n => n.Id).Should().Contain("ACME");
        results.Select(n => n.Id).Should().Contain("FutuRe");
        results.Should().NotContain(n => n.Id == "Contoso",
            "Contoso is not in searchable_schemas");
    }

    [Fact(Timeout = 60000)]
    public async Task CrossSchema_SearchAcrossSchemas_AccessControlFilters()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        await PopulateSearchableSchemas(schemas, ct);

        // Give testuser access only to ACME via partition_access.
        // (PG batched both statements into one command; the Snowflake driver executes exactly
        // one statement per command, so they run as two.)
        await _fixture.ConnectionSource.ExecuteNonQuery(
            $"DELETE FROM {PartitionAccessTable}", ct)
            .Should().Within(30.Seconds()).Emit();
        await _fixture.ConnectionSource.ExecuteNonQuery(
            $"INSERT INTO {PartitionAccessTable} (\"user_id\", \"partition\") SELECT 'testuser', 'acme'", ct)
            .Should().Within(30.Seconds()).Emit();

        // Also set up effective permissions for testuser in ACME schema.
        // (PG appended ON CONFLICT DO NOTHING; CleanDataAsync in the setup wiped the table,
        // so a plain guarded-free INSERT is equivalent here.)
        await _fixture.ConnectionSource.ExecuteNonQuery(
            "INSERT INTO \"acme\".\"user_effective_permissions\" (\"user_id\", \"node_path_prefix\", \"permission\", \"is_allow\") " +
            "SELECT 'testuser', 'ACME', 'Read', true", ct)
            .Should().Within(30.Seconds()).Emit();

        // Also register Markdown as public_read in all schemas
        // (delete-then-insert = PG's ON CONFLICT DO UPDATE upsert; node_type_permissions is not
        // wiped per schema by CleanDataAsync's central batch on PG either).
        foreach (var schema in schemas)
        {
            await _fixture.ConnectionSource.ExecuteNonQuery(
                $"DELETE FROM \"{schema}\".\"node_type_permissions\" WHERE \"node_type\" = 'Markdown'", ct)
                .Should().Within(30.Seconds()).Emit();
            await _fixture.ConnectionSource.ExecuteNonQuery(
                $"INSERT INTO \"{schema}\".\"node_type_permissions\" (\"node_type\", \"public_read\") " +
                "SELECT 'Markdown', true", ct)
                .Should().Within(30.Seconds()).Emit();
        }

        var results = await SearchAcrossSchemas(ParsedQuery.Empty, "testuser", ct);

        // testuser has partition_access only to ACME — should only see ACME nodes
        results.Should().NotBeEmpty();
        results.Select(n => n.Id).Should().Contain("ACME");

        // CRITICAL: public_read must NOT bypass partition_access.
        // testuser has NO partition_access to FutuRe or Contoso,
        // so those nodes must NOT appear even though Markdown is public_read.
        results.Should().NotContain(n => n.Id == "FutuRe",
            "testuser has no partition_access to FutuRe — public_read must not bypass partition check");
        results.Should().NotContain(n => n.Id == "Contoso",
            "testuser has no partition_access to Contoso — public_read must not bypass partition check");
        results.Should().NotContain(n => n.Id == "Report" && n.Namespace == "FutuRe",
            "FutuRe child nodes must also be hidden");
        results.Should().NotContain(n => n.Id == "Report" && n.Namespace == "Contoso",
            "Contoso child nodes must also be hidden");
    }

    [Fact(Timeout = 60000)]
    public async Task CrossSchema_NodeTypeFilter()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        await PopulateSearchableSchemas(schemas, ct);

        // PG inlined `LOWER(n.node_type) = 'markdown'`; the generator emits exactly that
        // predicate for a parsed nodeType filter.
        var results = await SearchAcrossSchemas(new QueryParser().Parse("nodeType:Markdown"), null, ct);

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(n => n.NodeType == "Markdown");
    }

    [Fact(Timeout = 60000)]
    public async Task CrossSchema_QueryNodesAcrossSchemas_ReturnsResults()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironment(ct);

        var schemas = partitions.Keys.Select(k => k.ToLowerInvariant()).ToList();
        var adapter = new SnowflakeStorageAdapter(
            _fixture.ConnectionSource, capabilities: _fixture.CapabilityHolder, options: _fixture.Options);
        var query = new QueryParser().Parse("is:main");

        var results = await adapter.QueryNodesAcrossSchemasAsync(query, _options, schemas, ct: ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();

        results.Should().NotBeEmpty("cross-schema query should return nodes");
        results.Select(n => n.Id).Should().Contain("ACME");
        results.Select(n => n.Id).Should().Contain("FutuRe");
        results.Select(n => n.Id).Should().Contain("Contoso");
    }

    // ── searchable_schemas sync throttle (PG: SearchableSchemasSyncThrottleTests) ─────────
    //
    // Reproduces the prod incident on 2026-05-20: opening a thread fans out to N cross-schema
    // queries, each of which previously called SyncSearchableSchemasAsync unconditionally.
    // Per-query SELECT + DELETE + INSERT on searchable_schemas melted the central connection
    // pool and made /authorize hang. The throttle (single-flight + TTL) MUST collapse N
    // concurrent calls into ONE actual DB sync — asserted on the in-process ActualSyncCount
    // counter, exactly like the PG twin.

    [Fact]
    public async Task SyncSearchableSchemasAsync_HundredConcurrentCalls_RunsActualSyncOnce()
    {
        _fixture.SkipUnlessAvailable();
        // Repro: 100 fan-outs hit the provider in the same render tick.
        // Without the throttle every one of them did a full DELETE+INSERT.
        // With the throttle we expect exactly 1 actual sync.
        var provider = CreateProvider();
        provider.SyncTtl = System.TimeSpan.FromSeconds(30);

        var tasks = new Task[100];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = provider.SyncSearchableSchemasAsync(CancellationToken.None);
        await Task.WhenAll(tasks);

        provider.ActualSyncCount.Should().Be(1,
            "all 100 concurrent calls within the TTL must collapse to one DB sync — "
            + "this is the throttle that fixes the prod thread-load deadlock");
    }

    [Fact]
    public async Task SyncSearchableSchemasAsync_AfterTtlElapses_RunsAgain()
    {
        _fixture.SkipUnlessAvailable();
        // Ensure the throttle doesn't permanently lock us out — a NEW partition
        // created mid-session must become visible after SyncTtl elapses.
        var provider = CreateProvider();
        // Tiny TTL so the test doesn't wait long; the prod default is 30 s.
        provider.SyncTtl = System.TimeSpan.FromMilliseconds(50);

        await provider.SyncSearchableSchemasAsync(CancellationToken.None);
        provider.ActualSyncCount.Should().Be(1);

        await Task.Delay(120, TestContext.Current.CancellationToken);

        await provider.SyncSearchableSchemasAsync(CancellationToken.None);
        provider.ActualSyncCount.Should().Be(2,
            "after the TTL elapses, the next call must actually sync so a newly-created "
            + "partition becomes visible to the cross-schema fan-out");
    }

    [Fact]
    public async Task SyncSearchableSchemasAsync_BurstAfterTtl_StillOneSyncPerWindow()
    {
        _fixture.SkipUnlessAvailable();
        // Walk through several windows verifying that each window costs exactly
        // one sync regardless of burst size. This is the load-shape the prod
        // thread-render generates: N parallel fan-outs per render, repeated as
        // the user scrolls / new messages stream in.
        var provider = CreateProvider();
        provider.SyncTtl = System.TimeSpan.FromMilliseconds(50);

        for (int window = 0; window < 3; window++)
        {
            var burst = new Task[20];
            for (int i = 0; i < burst.Length; i++)
                burst[i] = provider.SyncSearchableSchemasAsync(CancellationToken.None);
            await Task.WhenAll(burst);
            await Task.Delay(70, TestContext.Current.CancellationToken);
        }

        provider.ActualSyncCount.Should().BeInRange(3, 4,
            "three windows of 20 concurrent calls each = 3 (or 4 if a sync straddled "
            + "the boundary) actual syncs, never 60");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task PopulateSearchableSchemasAsync(IEnumerable<string> schemas, CancellationToken ct)
    {
        // One statement per command — the Snowflake driver executes no multi-statement batches.
        await using var connection = await _fixture.ConnectionSource.OpenAsync(ct);
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM {SearchableSchemasTable}";
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var schema in schemas)
        {
            // PG: INSERT … ON CONFLICT DO NOTHING → NOT-EXISTS-guarded INSERT here.
            await using var insert = connection.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO {SearchableSchemasTable} ("schema_name")
                SELECT :s FROM (SELECT 1 AS "x")
                WHERE NOT EXISTS (SELECT 1 FROM {SearchableSchemasTable} WHERE "schema_name" = :s)
                """;
            SnowflakeConnectionSource.AddParam(insert, "s", schema, DbType.String);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<List<MeshNode>> SearchAcrossSchemasAsync(
        ParsedQuery query, string? userId, CancellationToken ct)
    {
        var provider = CreateProvider();
        // Like the proc, the schema set comes from the searchable_schemas registry.
        var schemas = await provider.GetSearchableSchemasAsync(ct);

        var results = new List<MeshNode>();
        await foreach (var node in provider.QueryAcrossSchemasAsync(query, _options, schemas, userId, ct))
            results.Add(node);
        return results;
    }
}
