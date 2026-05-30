using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Reactive.Linq;
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

    /// <summary>
    /// Reactive replacement for <c>QueryAsync(...).ToListAsync()</c>: the first
    /// <see cref="QueryChangeType.Initial"/> emission carries the full snapshot.
    /// </summary>
    private IReadOnlyList<MeshNode> QueryNodes(string query)
        => MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

    [Fact(Timeout = 30000)]
    public void FanOut_SortByLastModified_MergesCorrectly()
    {
        // Arrange: create nodes in different namespaces with known timestamps.
        // The newest node is in a "later" namespace to simulate partition ordering issues.
        var baseTime = DateTimeOffset.UtcNow;

        // Namespace A: older items
        NodeFactory.CreateNode(MeshNode.FromPath("FoNsA/old1") with
        {
            Name = "Old Item A1", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-10)
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("FoNsA/old2") with
        {
            Name = "Old Item A2", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-8)
        }).Should().Emit();

        // Namespace B: newer items
        NodeFactory.CreateNode(MeshNode.FromPath("FoNsB/new1") with
        {
            Name = "New Item B1", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-1)
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("FoNsB/new2") with
        {
            Name = "New Item B2", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-2)
        }).Should().Emit();

        // Namespace C: middle items
        NodeFactory.CreateNode(MeshNode.FromPath("FoNsC/mid1") with
        {
            Name = "Mid Item C1", NodeType = "Markdown",
            LastModified = baseTime.AddMinutes(-5)
        }).Should().Emit();

        // Act: global query with sort and limit
        var results = QueryNodes("is:main scope:descendants sort:LastModified-desc limit:3");

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
    public void FanOut_NoLimit_ReturnsAllResults()
    {
        // Arrange: nodes in different namespaces
        NodeFactory.CreateNode(MeshNode.FromPath("FoAll1/doc1") with
        {
            Name = "Doc All 1", NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("FoAll2/doc2") with
        {
            Name = "Doc All 2", NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("FoAll3/doc3") with
        {
            Name = "Doc All 3", NodeType = "Markdown"
        }).Should().Emit();

        // Act: no limit
        var results = QueryNodes("is:main scope:descendants sort:LastModified-desc");

        // Assert: all 3 returned
        results.Should().HaveCountGreaterThanOrEqualTo(3);
        results.Select(n => n.Name).Should().Contain("Doc All 1");
        results.Select(n => n.Name).Should().Contain("Doc All 2");
        results.Select(n => n.Name).Should().Contain("Doc All 3");
    }

    [Fact(Timeout = 30000)]
    public void FanOut_TextSearch_FindsAcrossNamespaces()
    {
        // Arrange: "Unique" text in different namespaces
        NodeFactory.CreateNode(MeshNode.FromPath("FoTxt1/alpha") with
        {
            Name = "UniqueSearchTerm Alpha", NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("FoTxt2/beta") with
        {
            Name = "UniqueSearchTerm Beta", NodeType = "Markdown"
        }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("FoTxt3/gamma") with
        {
            Name = "No Match Here", NodeType = "Markdown"
        }).Should().Emit();

        // Act: text search across all namespaces
        var results = QueryNodes("UniqueSearchTerm is:main scope:descendants");

        // Assert: finds both matching nodes across namespaces
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain("UniqueSearchTerm Alpha");
        results.Select(n => n.Name).Should().Contain("UniqueSearchTerm Beta");
    }

    [Fact(Timeout = 30000)]
    public void FanOut_Deduplicates_SamePathAcrossProviders()
    {
        // Arrange: create a node (only one copy should appear)
        NodeFactory.CreateNode(MeshNode.FromPath("FoDup/item1") with
        {
            Name = "Deduplicate Me", NodeType = "Markdown"
        }).Should().Emit();

        // Act: query that hits all providers
        var results = QueryNodes("path:FoDup/item1");

        // Assert: only one result (deduplication by path)
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Deduplicate Me");
    }
}
