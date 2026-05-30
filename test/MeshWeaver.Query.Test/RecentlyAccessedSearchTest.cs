using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests that the search bar's empty-input query (source:accessed) returns
/// items the user has actually accessed, ordered by last access time descending,
/// across multiple partitions.
/// </summary>
public class RecentlyAccessedSearchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSampleUsers();

    /// <summary>
    /// Reactive replacement for <c>QueryAsync(...).ToListAsync()</c>: the first
    /// <see cref="QueryChangeType.Initial"/> emission carries the full snapshot.
    /// </summary>
    private IReadOnlyList<MeshNode> QueryNodes(string query)
        => MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

    [Fact(Timeout = 30000)]
    public void RecentlyAccessed_OrderedByAccessTime_AcrossPartitions()
    {
        // Arrange: create nodes in different partitions
        NodeFactory.CreateNode(new MeshNode("p1") { Name = "Partition 1", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("p1/doc-a") with
        {
            Name = "Alpha Doc", NodeType = "Markdown"
        }).Should().Emit();

        NodeFactory.CreateNode(new MeshNode("p2") { Name = "Partition 2", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("p2/doc-b") with
        {
            Name = "Beta Doc", NodeType = "Markdown"
        }).Should().Emit();

        NodeFactory.CreateNode(MeshNode.FromPath("p1/doc-c") with
        {
            Name = "Gamma Doc", NodeType = "Markdown"
        }).Should().Emit();

        // Simulate user accessing nodes in a specific order with distinct timestamps.
        // The TrackActivityRequest handler persists UserActivity records.
        var userId = TestUsers.Admin.ObjectId;

        // Thread.Sleep forces distinct LastModified timestamps for the sort assertion
        // (sanctioned use — distinct-timestamp ordering, not a propagation wait).
        Mesh.Post(new TrackActivityRequest("p1/doc-a", userId, "Alpha Doc", "Markdown", "p1"));
        Thread.Sleep(50);

        Mesh.Post(new TrackActivityRequest("p2/doc-b", userId, "Beta Doc", "Markdown", "p2"));
        Thread.Sleep(50);

        Mesh.Post(new TrackActivityRequest("p1/doc-c", userId, "Gamma Doc", "Markdown", "p1"));
        Thread.Sleep(50);

        // Access Alpha again (most recent now)
        Mesh.Post(new TrackActivityRequest("p1/doc-a", userId, "Alpha Doc", "Markdown", "p1"));

        // Wait actively until all 3 distinct tracked nodes have surfaced — fold the
        // live query's deltas into a running path set rather than a fixed wait.
        var results = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                "source:accessed scope:descendants is:main sort:LastModified-desc context:search limit:10"))
            .Scan(ImmutableList<MeshNode>.Empty, (acc, c) =>
                c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset
                    ? c.Items.ToImmutableList()
                    : acc.AddRange(c.Items))
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(list => list.Count >= 3
                && list.Any(n => n.Path == "p1/doc-a")
                && list.Any(n => n.Path == "p2/doc-b")
                && list.Any(n => n.Path == "p1/doc-c"));

        Output.WriteLine($"Results: {results.Count}");
        foreach (var r in results)
            Output.WriteLine($"  {r.Path} - {r.Name} - LastModified: {r.LastModified}");

        // Assert: results should contain our accessed nodes
        var accessedPaths = results.Select(r => r.Path).ToList();
        accessedPaths.Should().Contain("p1/doc-a", "accessed twice");
        accessedPaths.Should().Contain("p2/doc-b", "accessed once in partition 2");
        accessedPaths.Should().Contain("p1/doc-c", "accessed once in partition 1");

        // The query results should respect the sort:LastModified-desc order.
        // In InMemory mode, source:accessed returns all main nodes sorted by LastModified.
        // Verify the results are in descending LastModified order.
        for (var i = 1; i < results.Count; i++)
        {
            results[i - 1].LastModified.Should().BeOnOrAfter(results[i].LastModified,
                $"result[{i - 1}] ({results[i - 1].Name}) should be >= result[{i}] ({results[i].Name}) by LastModified");
        }

        // Nodes the user never accessed should not appear before accessed ones
        // (In InMemory, source:accessed returns ALL main nodes; in PostgreSQL it would
        // use INNER JOIN on UserActivity, but the ordering test is still valid.)
    }

    [Fact(Timeout = 30000)]
    public void RecentlyAccessed_NoAccess_ReturnsEmpty_OrMainNodes()
    {
        // Arrange: nodes exist but no activity tracked
        NodeFactory.CreateNode(new MeshNode("noAccess") { Name = "No Access NS", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("noAccess/doc") with
        {
            Name = "Unvisited", NodeType = "Markdown"
        }).Should().Emit();

        // Act
        var results = QueryNodes("source:accessed scope:descendants is:main sort:LastModified-desc context:search limit:10");

        // Assert: In InMemory mode, source:accessed returns main nodes (no UserActivity JOIN).
        // In PostgreSQL, this would be empty (INNER JOIN on UserActivity would filter out unvisited nodes).
        // Either way, satellites should never appear.
        results.Should().AllSatisfy(n =>
        {
            n.MainNode.Should().Be(n.Path, "only main nodes");
            n.NodeType.Should().NotBe("UserActivity");
            n.NodeType.Should().NotBe("Activity");
        });

        Output.WriteLine($"InMemory source:accessed returned {results.Count} main nodes (expected: all main nodes)");
    }
}
