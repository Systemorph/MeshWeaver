using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Npgsql;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that the "Latest Threads" query used by UserActivityLayoutAreas
/// finds threads across multiple partitions when filtering by content.createdBy.
/// Reproduces the production scenario where no threads appeared on the user dashboard.
/// </summary>
[Collection("PostgreSql")]
public class CrossPartitionThreadQueryTests
{
    private readonly PostgreSqlFixture _fixture;

    // Must use CamelCase to match hub serialization (Thread.CreatedBy â†’ "createdBy" in JSONB)
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CrossPartitionThreadQueryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Sets up 2 organization partitions, each with threads created by different users.
    /// </summary>
    private async Task<Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>>
        SetupPartitionsWithThreadsAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };

        var partitions = new Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>();

        // Create 2 org schemas: ACME and Northwind
        foreach (var org in new[] { "ACME", "Northwind" })
        {
            var schema = $"cp_thread_{org.ToLowerInvariant()}";
            var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
                schema,
                partitionDef with { Namespace = org, Schema = schema });
            partitions[org] = (ds, adapter);

            // Root org node
            await adapter.WriteAsync(new MeshNode(org)
            {
                Name = $"{org} Organization",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            }, _options, ct);
        }

        // Create threads by "Roland" in ACME
        await partitions["ACME"].Adapter.WriteAsync(new MeshNode("budget-review-a1b2", "ACME/_Thread")
        {
            Name = "Budget review for Q1",
            NodeType = "Thread",
            MainNode = "ACME",
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = "Roland" },
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-10),
        }, _options, ct);

        await partitions["ACME"].Adapter.WriteAsync(new MeshNode("project-planning-c3d4", "ACME/_Thread")
        {
            Name = "Project planning",
            NodeType = "Thread",
            MainNode = "ACME",
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = "Roland" },
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-5),
        }, _options, ct);

        // Create thread by "Roland" in Northwind
        await partitions["Northwind"].Adapter.WriteAsync(new MeshNode("sales-forecast-e5f6", "Northwind/_Thread")
        {
            Name = "Sales forecast discussion",
            NodeType = "Thread",
            MainNode = "Northwind",
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = "Roland" },
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-2),
        }, _options, ct);

        // Create thread by "Alice" in Northwind (should NOT appear for Roland)
        await partitions["Northwind"].Adapter.WriteAsync(new MeshNode("inventory-check-g7h8", "Northwind/_Thread")
        {
            Name = "Inventory check",
            NodeType = "Thread",
            MainNode = "Northwind",
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = "Alice" },
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-1),
        }, _options, ct);

        return partitions;
    }

    private Task<Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>>
        SetupPartitionsWithThreads(CancellationToken ct)
        => SetupPartitionsWithThreadsAsync(ct).Run().Should().Within(90.Seconds()).Emit();

    private Task<List<MeshNode>> QueryAdapter(PostgreSqlStorageAdapter adapter, ParsedQuery query, CancellationToken ct)
        => adapter.QueryNodesAsync(query, _options, ct: ct)
            .Collect(ct).Should().Within(30.Seconds()).Emit();

    [Fact(Timeout = 60000)]
    public async Task ContentCreatedBy_FindsThreadsInSinglePartition()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupPartitionsWithThreads(ct);

        // Query ACME partition only for Roland's threads
        var parser = new QueryParser();
        var query = parser.Parse("nodeType:Thread content.createdBy:Roland scope:subtree sort:LastModified-desc");

        var results = await QueryAdapter(partitions["ACME"].Adapter, query, ct);

        results.Should().HaveCount(2, "ACME has 2 threads by Roland");
        results.Should().AllSatisfy(n => n.NodeType.Should().Be("Thread"));
        results.Select(n => n.Name).Should().Contain("Budget review for Q1");
        results.Select(n => n.Name).Should().Contain("Project planning");
    }

    [Fact(Timeout = 60000)]
    public async Task ContentCreatedBy_FiltersOutOtherUsers()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupPartitionsWithThreads(ct);

        // Query Northwind for Roland's threads — should exclude Alice's thread
        var parser = new QueryParser();
        var query = parser.Parse("nodeType:Thread content.createdBy:Roland scope:subtree sort:LastModified-desc");

        var results = await QueryAdapter(partitions["Northwind"].Adapter, query, ct);

        results.Should().HaveCount(1, "only 1 of Northwind's 2 threads is by Roland");
        results[0].Name.Should().Be("Sales forecast discussion");
    }

    [Fact(Timeout = 60000)]
    public async Task ContentCreatedBy_CrossPartitionFanOut_FindsAllUserThreads()
    {
        var ct = TestContext.Current.CancellationToken;
        var partitions = await SetupPartitionsWithThreads(ct);

        // Simulate cross-partition fan-out: query each partition and merge results
        // This is what RoutingMeshQueryProvider does when no namespace is specified
        var parser = new QueryParser();
        var allResults = new List<MeshNode>();

        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = parser.Parse("nodeType:Thread content.createdBy:Roland scope:subtree sort:LastModified-desc");
            allResults.AddRange(await QueryAdapter(adapter, query, ct));
        }

        // Re-sort globally by LastModified desc (simulating merge)
        allResults = allResults
            .OrderByDescending(n => n.LastModified)
            .ToList();

        allResults.Should().HaveCount(3, "Roland has 3 threads across ACME (2) and Northwind (1)");
        allResults.Should().AllSatisfy(n => n.NodeType.Should().Be("Thread"));

        // Should NOT contain Alice's thread
        allResults.Should().NotContain(n => n.Name == "Inventory check");

        // Most recent first
        allResults[0].Name.Should().Be("Sales forecast discussion");
    }

    [Fact(Timeout = 60000)]
    public async Task ThreadWithoutCreatedBy_NotFoundByContentFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var schema = "cp_thread_nocreated";
        var (ds, adapter) = await _fixture.CreateSchemaAdapter(
            schema, partitionDef with { Namespace = "TestOrg", Schema = schema }, ct)
            .Should().Within(60.Seconds()).Emit();

        // Create a thread WITHOUT CreatedBy (reproduces the original bug)
        await adapter.Write(new MeshNode("orphan-thread-1234", "TestOrg/_Thread")
        {
            Name = "Orphan thread",
            NodeType = "Thread",
            MainNode = "TestOrg",
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = null },
        }, _options).Should().Within(30.Seconds()).Emit();

        var parser = new QueryParser();
        var query = parser.Parse("nodeType:Thread content.createdBy:Roland scope:subtree sort:LastModified-desc");

        var results = await QueryAdapter(adapter, query, ct);

        results.Should().BeEmpty("thread with null CreatedBy should not match content.createdBy:Roland");

        // But a query without createdBy filter should find it
        var allThreadsQuery = parser.Parse("nodeType:Thread scope:subtree");
        var allResults = await QueryAdapter(adapter, allThreadsQuery, ct);

        allResults.Should().HaveCount(1, "thread exists but has no createdBy");

        ds.Dispose();
    }

    [Fact(Timeout = 60000)]
    public async Task CamelCaseJsonKey_MatchesQuerySelector()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var schema = "cp_thread_case";
        var (ds, adapter) = await _fixture.CreateSchemaAdapter(
            schema, partitionDef with { Namespace = "CaseTest", Schema = schema }, ct)
            .Should().Within(60.Seconds()).Emit();

        // Write with CamelCase options (production behavior)
        await adapter.Write(new MeshNode("case-thread-abcd", "CaseTest/_Thread")
        {
            Name = "CamelCase thread",
            NodeType = "Thread",
            MainNode = "CaseTest",
            State = MeshNodeState.Active,
            Content = new MeshThread { CreatedBy = "Bob" },
        }, _options).Should().Within(30.Seconds()).Emit();

        // Verify the JSONB content has camelCase keys
        var storedValue = await ds.Probe(
            "SELECT content->>'createdBy' FROM threads WHERE id = 'case-thread-abcd'",
            System.Array.Empty<(string, object)>(),
            reader => reader.IsDBNull(0) ? null : reader.GetString(0), ct)
            .Should().Within(30.Seconds()).Emit();
        storedValue.Should().Be("Bob", "CamelCase serialization should store 'createdBy' (not 'CreatedBy')");

        // Query with lowercase selector should match
        var parser = new QueryParser();
        var query = parser.Parse("nodeType:Thread content.createdBy:Bob scope:subtree");
        var results = await QueryAdapter(adapter, query, ct);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("CamelCase thread");

        ds.Dispose();
    }
}
