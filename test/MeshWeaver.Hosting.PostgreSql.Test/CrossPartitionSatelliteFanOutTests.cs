using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

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

    // ── Thread fan-out ──────────────────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task NodeTypeThread_WithNamespace_QueriesThreadsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupPartitionsAsync(ct);

        // Insert threads in each partition
        await partitions["OrgAlpha"].Adapter.WriteAsync(new MeshNode("thread-a1", "OrgAlpha/_Thread")
        {
            Name = "Alpha Thread 1",
            NodeType = "Thread",
            MainNode = "OrgAlpha",
            State = MeshNodeState.Active,
            Content = new MeshThread { ParentPath = "OrgAlpha", CreatedBy = "user1" },
        }, _options, ct);

        await partitions["OrgBeta"].Adapter.WriteAsync(new MeshNode("thread-b1", "OrgBeta/_Thread")
        {
            Name = "Beta Thread 1",
            NodeType = "Thread",
            MainNode = "OrgBeta",
            State = MeshNodeState.Active,
            Content = new MeshThread { ParentPath = "OrgBeta", CreatedBy = "user1" },
        }, _options, ct);

        // Query each partition individually with namespace: prefix
        var parser = new QueryParser();

        var alphaQuery = parser.Parse("namespace:OrgAlpha nodeType:Thread sort:LastModified-desc");
        var alphaResults = await QueryAdapterAsync(partitions["OrgAlpha"].Adapter, alphaQuery, ct);

        var betaQuery = parser.Parse("namespace:OrgBeta nodeType:Thread sort:LastModified-desc");
        var betaResults = await QueryAdapterAsync(partitions["OrgBeta"].Adapter, betaQuery, ct);

        alphaResults.Should().ContainSingle(n => n.Name == "Alpha Thread 1");
        betaResults.Should().ContainSingle(n => n.Name == "Beta Thread 1");

        // Simulate fan-out: merge results from all partitions
        var allResults = alphaResults.Concat(betaResults)
            .OrderByDescending(n => n.LastModified).ToList();

        allResults.Should().HaveCount(2, "fan-out should find threads from both partitions");
        allResults.Should().OnlyContain(n => n.NodeType == "Thread");
    }

    // ── Comment fan-out ─────────────────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task NodeTypeComment_WithNamespace_QueriesAnnotationsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupPartitionsAsync(ct);

        // Insert comments in each partition's annotations table
        await partitions["OrgAlpha"].Adapter.WriteAsync(new MeshNode("cmt-a1", "OrgAlpha/doc1/_Comment")
        {
            Name = "Comment on Alpha doc",
            NodeType = "Comment",
            MainNode = "OrgAlpha/doc1",
            State = MeshNodeState.Active,
        }, _options, ct);

        await partitions["OrgBeta"].Adapter.WriteAsync(new MeshNode("cmt-b1", "OrgBeta/doc2/_Comment")
        {
            Name = "Comment on Beta doc",
            NodeType = "Comment",
            MainNode = "OrgBeta/doc2",
            State = MeshNodeState.Active,
        }, _options, ct);

        var parser = new QueryParser();

        var alphaQuery = parser.Parse("namespace:OrgAlpha nodeType:Comment sort:LastModified-desc");
        var alphaResults = await QueryAdapterAsync(partitions["OrgAlpha"].Adapter, alphaQuery, ct);

        var betaQuery = parser.Parse("namespace:OrgBeta nodeType:Comment sort:LastModified-desc");
        var betaResults = await QueryAdapterAsync(partitions["OrgBeta"].Adapter, betaQuery, ct);

        alphaResults.Should().ContainSingle(n => n.Name == "Comment on Alpha doc");
        betaResults.Should().ContainSingle(n => n.Name == "Comment on Beta doc");

        // Fan-out merged
        var allComments = alphaResults.Concat(betaResults).ToList();
        allComments.Should().HaveCount(2, "fan-out should find comments from both partitions");
        allComments.Should().OnlyContain(n => n.NodeType == "Comment");
    }

    // ── Activity fan-out ────────────────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task NodeTypeActivity_WithNamespace_QueriesActivitiesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupPartitionsAsync(ct);

        // Insert activities
        await partitions["OrgAlpha"].Adapter.WriteAsync(new MeshNode("act-a1", "OrgAlpha/doc1/_Activity")
        {
            Name = "Edit on Alpha doc",
            NodeType = "Activity",
            MainNode = "OrgAlpha/doc1",
            State = MeshNodeState.Active,
            Content = new ActivityLog("DataUpdate") { HubPath = "OrgAlpha/doc1" },
        }, _options, ct);

        await partitions["OrgBeta"].Adapter.WriteAsync(new MeshNode("act-b1", "OrgBeta/doc2/_Activity")
        {
            Name = "Edit on Beta doc",
            NodeType = "Activity",
            MainNode = "OrgBeta/doc2",
            State = MeshNodeState.Active,
            Content = new ActivityLog("Approval") { HubPath = "OrgBeta/doc2" },
        }, _options, ct);

        var parser = new QueryParser();

        var alphaQuery = parser.Parse("namespace:OrgAlpha nodeType:Activity sort:LastModified-desc");
        var alphaResults = await QueryAdapterAsync(partitions["OrgAlpha"].Adapter, alphaQuery, ct);

        var betaQuery = parser.Parse("namespace:OrgBeta nodeType:Activity sort:LastModified-desc");
        var betaResults = await QueryAdapterAsync(partitions["OrgBeta"].Adapter, betaQuery, ct);

        alphaResults.Should().ContainSingle(n => n.Name == "Edit on Alpha doc");
        betaResults.Should().ContainSingle(n => n.Name == "Edit on Beta doc");

        var allActivities = alphaResults.Concat(betaResults).ToList();
        allActivities.Should().HaveCount(2, "fan-out should find activities from both partitions");
        allActivities.Should().OnlyContain(n => n.NodeType == "Activity");
    }

    // ── Mixed: nodeType without namespace fans out correctly ─────────

    [Fact(Timeout = 60000)]
    public async Task NodeTypeOnly_WithoutNamespace_EachPartitionResolvesCorrectTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupPartitionsAsync(ct);

        // Insert threads
        await partitions["OrgAlpha"].Adapter.WriteAsync(new MeshNode("thr-noNs-a", "OrgAlpha/_Thread")
        {
            Name = "Thread NoNs Alpha",
            NodeType = "Thread",
            MainNode = "OrgAlpha",
            State = MeshNodeState.Active,
        }, _options, ct);
        await partitions["OrgBeta"].Adapter.WriteAsync(new MeshNode("thr-noNs-b", "OrgBeta/_Thread")
        {
            Name = "Thread NoNs Beta",
            NodeType = "Thread",
            MainNode = "OrgBeta",
            State = MeshNodeState.Active,
        }, _options, ct);

        // Also insert a main node to verify Thread query doesn't return main nodes
        await partitions["OrgAlpha"].Adapter.WriteAsync(new MeshNode("doc-main", "OrgAlpha")
        {
            Name = "Main Doc",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }, _options, ct);

        // Query without namespace — each adapter should resolve to threads table
        var parser = new QueryParser();
        var query = parser.Parse("nodeType:Thread sort:LastModified-desc");

        var alphaResults = await QueryAdapterAsync(partitions["OrgAlpha"].Adapter, query, ct);
        var betaResults = await QueryAdapterAsync(partitions["OrgBeta"].Adapter, query, ct);

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

    private static async Task<List<MeshNode>> QueryAdapterAsync(
        PostgreSqlStorageAdapter adapter, ParsedQuery query, CancellationToken ct)
    {
        var results = new List<MeshNode>();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await foreach (var node in adapter.QueryNodesAsync(query, options, ct: ct))
            results.Add(node);
        return results;
    }
}
