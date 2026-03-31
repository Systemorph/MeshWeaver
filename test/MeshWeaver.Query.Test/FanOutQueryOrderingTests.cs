using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests that global fan-out queries (across multiple namespaces/partitions)
/// correctly merge, re-sort, and limit results.
///
/// The core issue: each partition returns results in its own order.
/// The aggregator must re-sort the merged results before applying the global limit.
/// Without re-sorting, the limit cuts off results from slower-responding partitions.
/// </summary>
public class FanOutQueryOrderingTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(25.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact(Timeout = 30000)]
    public async Task FanOut_SortByLastModified_MergesCorrectly()
    {
        // Arrange: create nodes in different namespaces with known timestamps.
        // The newest node is in a "later" namespace to simulate partition ordering issues.
        var baseTime = DateTimeOffset.UtcNow;

        // Namespace A: older items
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoNsA/old1") with
        {
            Name = "Old Item A1", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-10)
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoNsA/old2") with
        {
            Name = "Old Item A2", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-8)
        }, TestTimeout);

        // Namespace B: newer items
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoNsB/new1") with
        {
            Name = "New Item B1", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-1)
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoNsB/new2") with
        {
            Name = "New Item B2", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-2)
        }, TestTimeout);

        // Namespace C: middle items
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoNsC/mid1") with
        {
            Name = "Mid Item C1", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-5)
        }, TestTimeout);

        // Act: global query with sort and limit
        var results = await MeshQuery
            .QueryAsync<MeshNode>("is:main scope:descendants sort:LastModified-desc limit:3")
            .ToListAsync();

        Output.WriteLine($"Results ({results.Count}):");
        foreach (var r in results)
            Output.WriteLine($"  {r.Path}: {r.Name} (modified={r.LastModified:HH:mm:ss})");

        // Assert: top 3 by LastModified descending should be B1, B2, C1
        results.Should().HaveCount(3, "limit:3 should return exactly 3");
        results[0].Name.Should().Be("New Item B1", "newest item should be first");
        results[1].Name.Should().Be("New Item B2", "second newest should be second");
        results[2].Name.Should().Be("Mid Item C1", "third newest should be third");
    }

    [Fact(Timeout = 30000)]
    public async Task FanOut_NoLimit_ReturnsAllResults()
    {
        // Arrange: nodes in different namespaces
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoAll1/doc1") with
        {
            Name = "Doc All 1", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoAll2/doc2") with
        {
            Name = "Doc All 2", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoAll3/doc3") with
        {
            Name = "Doc All 3", NodeType = "Markdown"
        }, TestTimeout);

        // Act: no limit
        var results = await MeshQuery
            .QueryAsync<MeshNode>("is:main scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Assert: all 3 returned
        results.Should().HaveCountGreaterThanOrEqualTo(3);
        results.Select(n => n.Name).Should().Contain("Doc All 1");
        results.Select(n => n.Name).Should().Contain("Doc All 2");
        results.Select(n => n.Name).Should().Contain("Doc All 3");
    }

    [Fact(Timeout = 30000)]
    public async Task FanOut_TextSearch_FindsAcrossNamespaces()
    {
        // Arrange: "Unique" text in different namespaces
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoTxt1/alpha") with
        {
            Name = "UniqueSearchTerm Alpha", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoTxt2/beta") with
        {
            Name = "UniqueSearchTerm Beta", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoTxt3/gamma") with
        {
            Name = "No Match Here", NodeType = "Markdown"
        }, TestTimeout);

        // Act: text search across all namespaces
        var results = await MeshQuery
            .QueryAsync<MeshNode>("UniqueSearchTerm is:main scope:descendants")
            .ToListAsync();

        // Assert: finds both matching nodes across namespaces
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain("UniqueSearchTerm Alpha");
        results.Select(n => n.Name).Should().Contain("UniqueSearchTerm Beta");
    }

    [Fact(Timeout = 30000)]
    public async Task FanOut_Deduplicates_SamePathAcrossProviders()
    {
        // Arrange: create a node (only one copy should appear)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("FoDup/item1") with
        {
            Name = "Deduplicate Me", NodeType = "Markdown"
        }, TestTimeout);

        // Act: query that hits all providers
        var results = await MeshQuery
            .QueryAsync<MeshNode>("path:FoDup/item1")
            .ToListAsync();

        // Assert: only one result (deduplication by path)
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Deduplicate Me");
    }
}
