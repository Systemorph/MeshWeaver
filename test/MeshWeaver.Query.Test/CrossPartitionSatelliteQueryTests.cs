using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests that satellite node type queries (Thread, Comment, etc.)
/// fan out across all visible partitions, not just a single namespace.
/// When querying `nodeType:Thread` without a specific namespace,
/// the query system must search _Thread tables in every partition.
/// </summary>
public class CrossPartitionSatelliteQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminUserId = "Roland";

    private CancellationToken TestTimeout => new CancellationTokenSource(25.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSampleUsers()
            .AddAI();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    // ── Thread fan-out ──────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeTypeThread_FansOutAcrossAllPartitions()
    {
        // Arrange: create two partitions with threads in each
        await NodeFactory.CreateNodeAsync(
            new MeshNode("PartitionA") { Name = "Partition A", NodeType = "Markdown" }, TestTimeout);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("PartitionB") { Name = "Partition B", NodeType = "Markdown" }, TestTimeout);

        var client = GetClient();

        var resp1 = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("PartitionA", "Thread in A", AdminUserId)),
            o => o.WithTarget(new Address("PartitionA")), TestTimeout);
        resp1.Message.Success.Should().BeTrue(resp1.Message.Error);
        var threadA = resp1.Message.Node!.Path!;
        Output.WriteLine($"Thread A: {threadA}");

        var resp2 = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("PartitionB", "Thread in B", AdminUserId)),
            o => o.WithTarget(new Address("PartitionB")), TestTimeout);
        resp2.Message.Success.Should().BeTrue(resp2.Message.Error);
        var threadB = resp2.Message.Node!.Path!;
        Output.WriteLine($"Thread B: {threadB}");

        // Act: query nodeType:Thread without namespace — should fan out to all _Thread tables
        var results = await MeshQuery
            .QueryAsync<MeshNode>("nodeType:Thread sort:LastModified-desc")
            .ToListAsync();

        Output.WriteLine($"nodeType:Thread => {results.Count} results: [{string.Join(", ", results.Select(r => r.Path))}]");

        // Assert: threads from BOTH partitions should appear
        results.Should().Contain(n => n.Path == threadA, "Thread from PartitionA should be found");
        results.Should().Contain(n => n.Path == threadB, "Thread from PartitionB should be found");
        results.Should().OnlyContain(n => n.NodeType == "Thread");
    }

    [Fact(Timeout = 30000)]
    public async Task NodeTypeThread_WithNamespace_SearchesSinglePartition()
    {
        // Arrange: threads in two partitions
        await NodeFactory.CreateNodeAsync(
            new MeshNode("NsX") { Name = "Namespace X", NodeType = "Markdown" }, TestTimeout);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("NsY") { Name = "Namespace Y", NodeType = "Markdown" }, TestTimeout);

        var client = GetClient();

        var resp1 = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("NsX", "X thread", AdminUserId)),
            o => o.WithTarget(new Address("NsX")), TestTimeout);
        resp1.Message.Success.Should().BeTrue(resp1.Message.Error);

        var resp2 = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("NsY", "Y thread", AdminUserId)),
            o => o.WithTarget(new Address("NsY")), TestTimeout);
        resp2.Message.Success.Should().BeTrue(resp2.Message.Error);

        // Act: query with explicit namespace — should only return threads from NsX
        var results = await MeshQuery
            .QueryAsync<MeshNode>("namespace:NsX nodeType:Thread sort:LastModified-desc")
            .ToListAsync();

        Output.WriteLine($"namespace:NsX nodeType:Thread => {results.Count} results");

        // Assert: only NsX thread
        results.Should().OnlyContain(n => n.Path!.StartsWith("NsX/"),
            "namespace-scoped query should only return threads from that namespace");
    }

    // ── Comment fan-out ─────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeTypeComment_FansOutAcrossAllPartitions()
    {
        // Arrange: create nodes with comments in different partitions
        await NodeFactory.CreateNodeAsync(
            new MeshNode("CmtOrgA") { Name = "Org A", NodeType = "Markdown" }, TestTimeout);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("CmtOrgB") { Name = "Org B", NodeType = "Markdown" }, TestTimeout);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("CmtOrgA/doc1") with
        {
            Name = "Doc A", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("CmtOrgB/doc2") with
        {
            Name = "Doc B", NodeType = "Markdown"
        }, TestTimeout);

        // Create comments as satellite nodes
        var commentA = MeshNode.FromPath($"CmtOrgA/doc1/_Comment/cmt-{Guid.NewGuid():N}") with
        {
            Name = "Comment on A",
            NodeType = "Comment",
            MainNode = "CmtOrgA/doc1"
        };
        var commentB = MeshNode.FromPath($"CmtOrgB/doc2/_Comment/cmt-{Guid.NewGuid():N}") with
        {
            Name = "Comment on B",
            NodeType = "Comment",
            MainNode = "CmtOrgB/doc2"
        };

        await NodeFactory.CreateNodeAsync(commentA, TestTimeout);
        await NodeFactory.CreateNodeAsync(commentB, TestTimeout);

        // Act: query nodeType:Comment without namespace
        var results = await MeshQuery
            .QueryAsync<MeshNode>("nodeType:Comment sort:LastModified-desc")
            .ToListAsync();

        Output.WriteLine($"nodeType:Comment => {results.Count} results: [{string.Join(", ", results.Select(r => r.Path))}]");

        // Assert: comments from both partitions
        results.Should().Contain(n => n.Path == commentA.Path, "Comment from CmtOrgA should be found");
        results.Should().Contain(n => n.Path == commentB.Path, "Comment from CmtOrgB should be found");
        results.Should().OnlyContain(n => n.NodeType == "Comment");
    }
}
