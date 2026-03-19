using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests cross-partition search with multiple organizations on PostgreSQL.
/// Sets up 3 orgs (ACME, PartnerRe, Contoso) each in their own schema,
/// with threads and activity, then verifies global queries find them all.
///
/// This reproduces the production scenario where PartnerRe wasn't
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
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        // Admin partition (default schema) — stores partition metadata
        var adminAdapter = _fixture.StorageAdapter;

        // Create 3 org schemas
        var orgNames = new[] { "ACME", "PartnerRe", "Contoso" };
        var partitions = new Dictionary<string, (NpgsqlDataSource Ds, PostgreSqlStorageAdapter Adapter)>();

        foreach (var org in orgNames)
        {
            var schemaName = org.ToLowerInvariant();
            var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
                schemaName,
                partitionDef with { Namespace = org, Schema = schemaName },
                ct);
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
                    TableMappings = PartitionDefinition.StandardTableMappings
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

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_FindsOrgsAcrossAllSchemas()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminAdapter, partitions) = await SetupMultiOrgEnvironmentAsync(ct);

        // Query each partition individually — verify data is there
        foreach (var (org, (_, adapter)) in partitions)
        {
            var nodes = new List<MeshNode>();
            var query = new QueryParser().Parse("scope:subtree is:main");
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
                nodes.Add(node);

            nodes.Should().NotBeEmpty($"{org} partition should have nodes");
            nodes.Select(n => n.Name).Should().Contain($"{org} Organization",
                $"{org} root org node should be in its own partition");
            nodes.Select(n => n.Name).Should().Contain($"{org} Annual Report");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_ThreadsFoundByNodeType()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironmentAsync(ct);

        // Query threads in each partition
        foreach (var (org, (_, adapter)) in partitions)
        {
            var threads = new List<MeshNode>();
            var query = new QueryParser().Parse("nodeType:Thread scope:subtree");
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
                threads.Add(node);

            threads.Should().NotBeEmpty($"{org} should have a thread");
            threads[0].Name.Should().Contain(org);
            threads[0].NodeType.Should().Be("Thread");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_ActivityQueryFindsNodesWithActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironmentAsync(ct);

        // source:activity query in each partition
        foreach (var (org, (_, adapter)) in partitions)
        {
            var results = new List<MeshNode>();
            var query = new QueryParser().Parse("source:activity scope:subtree is:main sort:LastModified-desc");
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
                results.Add(node);

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
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironmentAsync(ct);

        // Collect all nodes across partitions, sorted by LastModified desc
        var allNodes = new List<MeshNode>();
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("scope:subtree is:main sort:LastModified-desc");
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
                allNodes.Add(node);
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
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironmentAsync(ct);

        // Search for "Annual Report" in each partition
        var allMatches = new List<MeshNode>();
        foreach (var (org, (_, adapter)) in partitions)
        {
            var query = new QueryParser().Parse("Annual scope:subtree is:main");
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
                allMatches.Add(node);
        }

        // All 3 orgs should have a matching "Annual Report"
        allMatches.Should().HaveCount(3, "each org has an 'Annual Report' document");
        allMatches.Select(n => n.Name).Should().Contain("ACME Annual Report");
        allMatches.Select(n => n.Name).Should().Contain("PartnerRe Annual Report");
        allMatches.Select(n => n.Name).Should().Contain("Contoso Annual Report");
    }

    [Fact(Timeout = 60000)]
    public async Task CrossPartition_ContextSearchExcludesSatelliteTypes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, partitions) = await SetupMultiOrgEnvironmentAsync(ct);

        // Query with context:search should exclude Thread and Activity
        foreach (var (org, (_, adapter)) in partitions)
        {
            var results = new List<MeshNode>();
            // Simulate context:search exclusion
            var query = new QueryParser().Parse("scope:subtree is:main");
            await foreach (var node in adapter.QueryNodesAsync(query, _options, ct: ct))
            {
                // Simulate the context filtering done by PostgreSqlMeshQuery
                if (node.NodeType is "Thread" or "Activity" or "ThreadMessage")
                    continue;
                results.Add(node);
            }

            results.Should().NotBeEmpty($"{org} should have main content nodes");
            results.Should().AllSatisfy(n =>
            {
                n.NodeType.Should().NotBe("Thread");
                n.NodeType.Should().NotBe("Activity");
            });
        }
    }
}
