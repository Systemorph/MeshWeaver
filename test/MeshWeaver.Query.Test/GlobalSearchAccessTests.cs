using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests that the top search bar (global search) correctly:
/// - Excludes Partition nodes from search context
/// - Returns only nodes the user can access
/// - Excludes satellite types from search context
/// - Finds main content nodes across all accessible partitions
/// </summary>
public class GlobalSearchAccessTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(25.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    // ── Partition nodes excluded from search ───────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task SearchContext_ExcludesPartitionNodes()
    {
        // Arrange: create a Partition node and a regular content node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("Admin/Partition/TestPartition") with
        {
            Name = "Test Partition",
            NodeType = "Partition",
            Content = new PartitionDefinition { Namespace = "TestPartition", DataSource = "default" }
        }, TestTimeout);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("TestPartition/doc1") with
        {
            Name = "Test Document", NodeType = "Markdown"
        }, TestTimeout);

        // Act: search with context:search (like the top search bar)
        var results = await MeshQuery
            .QueryAsync<MeshNode>("scope:descendants context:search sort:LastModified-desc limit:50")
            .ToListAsync();
        Output.WriteLine($"context:search returned {results.Count} results");
        foreach (var r in results.Take(20))
            Output.WriteLine($"  {r.Path} ({r.NodeType})");

        // Assert: Partition nodes excluded from search context, content nodes included
        results.Should().NotContain(n => n.NodeType == "Partition",
            "Partition nodes should be excluded from search context");
        results.Select(n => n.Name).Should().Contain("Test Document");
    }

    // ── Search context excludes satellite types ────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task SearchContext_ExcludesSatelliteTypes()
    {
        // Arrange: create main content + satellite nodes
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("satCtx/project") with
        {
            Name = "My Project", NodeType = "Markdown"
        }, TestTimeout);

        // Activity satellite
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("satCtx/project/_activity/log1") with
        {
            Name = "Activity Log", NodeType = "Activity",
            MainNode = "satCtx/project",
            Content = new ActivityLog("DataUpdate") { HubPath = "satCtx/project" }
        }, TestTimeout);

        // Thread satellite (created directly, not via request)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("satCtx/_Thread/test-thread-1234") with
        {
            Name = "Test Thread", NodeType = "Thread",
            MainNode = "satCtx/_Thread",
            Content = new AI.Thread { ParentPath = "satCtx", CreatedBy = "Roland" }
        }, TestTimeout);

        // Act: search with context:search (mimics the top search bar)
        var results = await MeshQuery
            .QueryAsync<MeshNode>("namespace:satCtx scope:descendants context:search sort:LastModified-desc")
            .ToListAsync();
        Output.WriteLine($"context:search returned {results.Count} results");
        foreach (var r in results)
            Output.WriteLine($"  {r.Path} ({r.NodeType})");

        // Assert: only main content nodes, no satellites
        results.Should().AllSatisfy(n =>
        {
            n.NodeType.Should().NotBe("Activity", "Activity is excluded from search context");
            n.NodeType.Should().NotBe("Thread", "Thread is excluded from search context");
            n.NodeType.Should().NotBe("ThreadMessage", "ThreadMessage is excluded from search context");
        });
        results.Select(n => n.Name).Should().Contain("My Project");
    }

    // ── Autocomplete finds main nodes ──────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_FindsMainContentNodes()
    {
        // Arrange: create some content nodes
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("acSearch/report") with
        {
            Name = "Annual Report", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("acSearch/budget") with
        {
            Name = "Budget Plan", NodeType = "Markdown"
        }, TestTimeout);

        // Act: autocomplete with prefix "Annual" (like typing in search bar)
        var suggestions = await MeshQuery
            .AutocompleteAsync("acSearch", "Annual", AutocompleteMode.RelevanceFirst, 10)
            .ToListAsync();
        Output.WriteLine($"Autocomplete 'Annual': {suggestions.Count} suggestions");
        foreach (var s in suggestions)
            Output.WriteLine($"  {s.Path}: {s.Name} (score={s.Score})");

        // Assert: should find the Annual Report
        suggestions.Should().Contain(s => s.Name == "Annual Report",
            "autocomplete should find nodes matching the prefix");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_WithSearchContext_ExcludesSatellites()
    {
        // Arrange: create content + satellite
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("acCtx/analysis") with
        {
            Name = "Risk Analysis", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("acCtx/analysis/_activity/log1") with
        {
            Name = "Risk Activity", NodeType = "Activity",
            MainNode = "acCtx/analysis",
            Content = new ActivityLog("DataUpdate") { HubPath = "acCtx/analysis" }
        }, TestTimeout);

        // Act: autocomplete with context:search (like the search bar does)
        var suggestions = await MeshQuery
            .AutocompleteAsync("acCtx", "Risk", AutocompleteMode.RelevanceFirst, 10, context: "search")
            .ToListAsync();
        Output.WriteLine($"Autocomplete 'Risk' context:search: {suggestions.Count} suggestions");
        foreach (var s in suggestions)
            Output.WriteLine($"  {s.Path}: {s.Name} ({s.NodeType})");

        // Assert: should find content node, not activity satellite
        suggestions.Should().Contain(s => s.Name == "Risk Analysis");
        suggestions.Should().NotContain(s => s.NodeType == "Activity",
            "Activity satellites should be excluded from search context");
    }

    // ── Global search returns main nodes only ──────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task GlobalSearch_IsMain_ExcludesSatelliteNodes()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("gbl/item1") with
        {
            Name = "Content Item", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("gbl/item1/_activity/log1") with
        {
            Name = "Activity", NodeType = "Activity",
            MainNode = "gbl/item1",
            Content = new ActivityLog("DataUpdate") { HubPath = "gbl/item1" }
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("gbl/item1/_Comment/c1") with
        {
            Name = "Comment", NodeType = "Comment",
            MainNode = "gbl/item1"
        }, TestTimeout);

        // Act: global search with is:main (the default for fan-out)
        var results = await MeshQuery
            .QueryAsync<MeshNode>("namespace:gbl is:main scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Assert: only main content nodes
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Content Item");
        results[0].MainNode.Should().Be(results[0].Path);
    }

    // ── Search across multiple partitions ──────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task GlobalSearch_FindsNodesAcrossMultipleNamespaces()
    {
        // Arrange: create nodes in different namespaces
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("SearchNs1/doc1") with
        {
            Name = "Alpha Document", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("SearchNs2/doc2") with
        {
            Name = "Beta Document", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("SearchNs3/doc3") with
        {
            Name = "Gamma Document", NodeType = "Markdown"
        }, TestTimeout);

        // Act: text search for "Document" across all namespaces
        var results = await MeshQuery
            .QueryAsync<MeshNode>("Document is:main scope:descendants sort:LastModified-desc")
            .ToListAsync();
        Output.WriteLine($"Global search 'Document': {results.Count} results");

        // Assert: should find nodes across namespaces
        results.Should().HaveCountGreaterThanOrEqualTo(3,
            "should find documents across all accessible namespaces");
        results.Select(n => n.Name).Should().Contain("Alpha Document");
        results.Select(n => n.Name).Should().Contain("Beta Document");
        results.Select(n => n.Name).Should().Contain("Gamma Document");
    }
}
