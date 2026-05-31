using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that satellite node type queries (Thread, Comment, Activity)
/// with namespace: prefix correctly resolve to the satellite table
/// in each partition. Verifies that per-partition fan-out picks
/// the correct table based on nodeType.
/// </summary>
[Collection("PostgreSql")]
public class CrossPartitionSatelliteFanOutTests
{
    private readonly PostgreSqlFixture _fixture;

    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CrossPartitionSatelliteFanOutTests(PostgreSqlFixture fixture) => _fixture = fixture;

    private Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>
        SetupPartitions(CancellationToken ct)
        => SetupPartitionsAsync(ct).Run().Should().Within(90.Seconds()).Emit();

    private async Task<Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>>
        SetupPartitionsAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();

        var partitions = new Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>();

        foreach (var org in new[] { "OrgAlpha", "OrgBeta" })
        {
            var schema = $"fanout_{org.ToLowerInvariant()}";
            var partitionDef = new PartitionDefinition
            {
                Namespace = org,
                Schema = schema,
                TableMappings = PartitionDefinition.StandardTableMappings,
            };
            var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(schema, partitionDef, ct);

            // CleanDataAsync only truncates the public schema. Per-test fan-out
            // schemas use CREATE SCHEMA IF NOT EXISTS, so satellite-table rows
            // from prior tests in the same xUnit run leak in (e.g. the Thread
            // test counting Org-rooted threads picks up the NoNs test's
            // leftovers and fails HaveCount(2)). Truncate every table in this
            // schema before the test seeds its own data.
            await using (var truncateCmd = ds.CreateCommand($"""
                DO $$
                DECLARE r record;
                BEGIN
                  FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = '{schema}' LOOP
                    EXECUTE 'TRUNCATE TABLE "{schema}"."' || r.tablename || '" CASCADE';
                  END LOOP;
                END$$;
                """))
                await truncateCmd.ExecuteNonQueryAsync(ct);

            partitions[org] = (ds, adapter);

            // Root node
            await adapter.WriteAsync(new MeshNode(org)
            {
                Name = $"{org} Organization",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            }, _options, ct);
        }

        return partitions;
    }

    // â”€â”€ Thread fan-out â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 60000)]
    public void NodeTypeThread_WithNamespace_QueriesThreadsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = SetupPartitions(ct);

        // Insert threads in each partition
        partitions["OrgAlpha"].Adapter.Write(new MeshNode("thread-a1", "OrgAlpha/_Thread")
        {
            Name = "Alpha Thread 1",
            NodeType = "Thread",
            MainNode = "OrgAlpha",
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = "user1" },
        }, _options).Should().Within(30.Seconds()).Emit();

        partitions["OrgBeta"].Adapter.Write(new MeshNode("thread-b1", "OrgBeta/_Thread")
        {
            Name = "Beta Thread 1",
            NodeType = "Thread",
            MainNode = "OrgBeta",
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = "user1" },
        }, _options).Should().Within(30.Seconds()).Emit();

        // Query each partition individually with namespace: prefix
        var parser = new QueryParser();

        var alphaQuery = parser.Parse("namespace:OrgAlpha nodeType:Thread sort:LastModified-desc");
        var alphaResults = QueryAdapter(partitions["OrgAlpha"].Adapter, alphaQuery, ct);

        var betaQuery = parser.Parse("namespace:OrgBeta nodeType:Thread sort:LastModified-desc");
        var betaResults = QueryAdapter(partitions["OrgBeta"].Adapter, betaQuery, ct);

        alphaResults.Should().ContainSingle(n => n.Name == "Alpha Thread 1");
        betaResults.Should().ContainSingle(n => n.Name == "Beta Thread 1");

        // Simulate fan-out: merge results from all partitions
        var allResults = alphaResults.Concat(betaResults)
            .OrderByDescending(n => n.LastModified).ToList();

        allResults.Should().HaveCount(2, "fan-out should find threads from both partitions");
        allResults.Should().OnlyContain(n => n.NodeType == "Thread");
    }

    // â”€â”€ Comment fan-out â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 60000)]
    public void NodeTypeComment_WithNamespace_QueriesAnnotationsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = SetupPartitions(ct);

        // Insert comments in each partition's annotations table
        partitions["OrgAlpha"].Adapter.Write(new MeshNode("cmt-a1", "OrgAlpha/doc1/_Comment")
        {
            Name = "Comment on Alpha doc",
            NodeType = "Comment",
            MainNode = "OrgAlpha/doc1",
            State = MeshNodeState.Active,
        }, _options).Should().Within(30.Seconds()).Emit();

        partitions["OrgBeta"].Adapter.Write(new MeshNode("cmt-b1", "OrgBeta/doc2/_Comment")
        {
            Name = "Comment on Beta doc",
            NodeType = "Comment",
            MainNode = "OrgBeta/doc2",
            State = MeshNodeState.Active,
        }, _options).Should().Within(30.Seconds()).Emit();

        var parser = new QueryParser();

        var alphaQuery = parser.Parse("namespace:OrgAlpha nodeType:Comment sort:LastModified-desc");
        var alphaResults = QueryAdapter(partitions["OrgAlpha"].Adapter, alphaQuery, ct);

        var betaQuery = parser.Parse("namespace:OrgBeta nodeType:Comment sort:LastModified-desc");
        var betaResults = QueryAdapter(partitions["OrgBeta"].Adapter, betaQuery, ct);

        alphaResults.Should().ContainSingle(n => n.Name == "Comment on Alpha doc");
        betaResults.Should().ContainSingle(n => n.Name == "Comment on Beta doc");

        // Fan-out merged
        var allComments = alphaResults.Concat(betaResults).ToList();
        allComments.Should().HaveCount(2, "fan-out should find comments from both partitions");
        allComments.Should().OnlyContain(n => n.NodeType == "Comment");
    }

    // â”€â”€ Activity fan-out â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 60000)]
    public void NodeTypeActivity_WithNamespace_QueriesActivitiesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = SetupPartitions(ct);

        // Insert activities
        partitions["OrgAlpha"].Adapter.Write(new MeshNode("act-a1", "OrgAlpha/doc1/_Activity")
        {
            Name = "Edit on Alpha doc",
            NodeType = "Activity",
            MainNode = "OrgAlpha/doc1",
            State = MeshNodeState.Active,
            Content = new ActivityLog("DataUpdate") { HubPath = "OrgAlpha/doc1" },
        }, _options).Should().Within(30.Seconds()).Emit();

        partitions["OrgBeta"].Adapter.Write(new MeshNode("act-b1", "OrgBeta/doc2/_Activity")
        {
            Name = "Edit on Beta doc",
            NodeType = "Activity",
            MainNode = "OrgBeta/doc2",
            State = MeshNodeState.Active,
            Content = new ActivityLog("Approval") { HubPath = "OrgBeta/doc2" },
        }, _options).Should().Within(30.Seconds()).Emit();

        var parser = new QueryParser();

        var alphaQuery = parser.Parse("namespace:OrgAlpha nodeType:Activity sort:LastModified-desc");
        var alphaResults = QueryAdapter(partitions["OrgAlpha"].Adapter, alphaQuery, ct);

        var betaQuery = parser.Parse("namespace:OrgBeta nodeType:Activity sort:LastModified-desc");
        var betaResults = QueryAdapter(partitions["OrgBeta"].Adapter, betaQuery, ct);

        alphaResults.Should().ContainSingle(n => n.Name == "Edit on Alpha doc");
        betaResults.Should().ContainSingle(n => n.Name == "Edit on Beta doc");

        var allActivities = alphaResults.Concat(betaResults).ToList();
        allActivities.Should().HaveCount(2, "fan-out should find activities from both partitions");
        allActivities.Should().OnlyContain(n => n.NodeType == "Activity");
    }

    // â”€â”€ Mixed: nodeType without namespace fans out correctly â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact(Timeout = 60000)]
    public void NodeTypeOnly_WithoutNamespace_EachPartitionResolvesCorrectTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = SetupPartitions(ct);

        // Insert threads
        partitions["OrgAlpha"].Adapter.Write(new MeshNode("thr-noNs-a", "OrgAlpha/_Thread")
        {
            Name = "Thread NoNs Alpha",
            NodeType = "Thread",
            MainNode = "OrgAlpha",
            State = MeshNodeState.Active,
        }, _options).Should().Within(30.Seconds()).Emit();
        partitions["OrgBeta"].Adapter.Write(new MeshNode("thr-noNs-b", "OrgBeta/_Thread")
        {
            Name = "Thread NoNs Beta",
            NodeType = "Thread",
            MainNode = "OrgBeta",
            State = MeshNodeState.Active,
        }, _options).Should().Within(30.Seconds()).Emit();

        // Also insert a main node to verify Thread query doesn't return main nodes
        partitions["OrgAlpha"].Adapter.Write(new MeshNode("doc-main", "OrgAlpha")
        {
            Name = "Main Doc",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }, _options).Should().Within(30.Seconds()).Emit();

        // Query without namespace â€” each adapter should resolve to threads table
        var parser = new QueryParser();
        var query = parser.Parse("nodeType:Thread sort:LastModified-desc");

        var alphaResults = QueryAdapter(partitions["OrgAlpha"].Adapter, query, ct);
        var betaResults = QueryAdapter(partitions["OrgBeta"].Adapter, query, ct);

        // Each partition returns only threads (from threads table), not main nodes
        alphaResults.Should().ContainSingle(n => n.Name == "Thread NoNs Alpha");
        alphaResults.Should().NotContain(n => n.NodeType == "Markdown",
            "nodeType:Thread should query threads table, not mesh_nodes");

        betaResults.Should().ContainSingle(n => n.Name == "Thread NoNs Beta");

        // Merged fan-out
        var merged = alphaResults.Concat(betaResults).ToList();
        merged.Should().HaveCount(2);
        merged.Should().OnlyContain(n => n.NodeType == "Thread");
    }

    private static List<MeshNode> QueryAdapter(
        PostgreSqlStorageAdapter adapter, ParsedQuery query, CancellationToken ct)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return adapter.QueryNodesAsync(query, options, ct: ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();
    }
}
