using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests that plans are stored as Markdown nodes under the thread partition.
/// The store_plan tool creates a node at {threadPath}/Plan with nodeType=Markdown.
/// This mirrors Claude Code's plan mode: Planner (Opus) produces a plan,
/// stores it for reference, and the user approves before Worker executes.
/// </summary>
public class PlanStorageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddAI();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    [Fact]
    public async Task StorePlan_CreatesMarkdownNodeUnderThread()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // 1. Create a context node
        var contextPath = "PlanTestOrg";
        await NodeFactory.CreateNode(
            new MeshNode(contextPath) { Name = "Plan Test Org", NodeType = "Markdown" });

        // 2. Create a thread under the context
        var client = GetClient();
        var threadResponse = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Plan a project setup")), o => o.WithTarget(new Address(contextPath))).FirstAsync().ToTask();

        threadResponse.Message.Success.Should().BeTrue(threadResponse.Message.Error);
        var threadPath = threadResponse.Message.Node!.Path;
        Output.WriteLine($"Thread created at: {threadPath}");

        // 3. Store a plan as a Markdown node under the thread
        var planContent = @"## Plan: Set up project structure

### Steps
1. Create Engineering department â€” `Create` â€” node under PlanTestOrg
2. Create Marketing department â€” `Create` â€” node under PlanTestOrg
3. Create README pages â€” `Create` â€” one per department

### Notes
- All nodes use Markdown type
- Verify each creation with Get";

        var planNode = new MeshNode("Plan", threadPath)
        {
            Name = "Execution Plan",
            NodeType = "Markdown",
            Content = planContent
        };
        await NodeFactory.CreateNode(planNode);

        // 4. Verify the plan node exists at {threadPath}/Plan
        var expectedPath = $"{threadPath}/Plan";
        Output.WriteLine($"Expected plan path: {expectedPath}");

        var retrievedNode = await ReadNodeAsync(expectedPath, ct);

        retrievedNode.Should().NotBeNull("plan node should be retrievable at {threadPath}/Plan");
        retrievedNode!.Path.Should().Be(expectedPath);
        retrievedNode.NodeType.Should().Be("Markdown");
        retrievedNode.Name.Should().Be("Execution Plan");

        // 5. Verify the content is the plan markdown
        var content = retrievedNode.Content;
        content.Should().NotBeNull();
        content.ToString().Should().Contain("Set up project structure");
    }

    [Fact]
    public async Task StorePlan_PlanIsInThreadPartition()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create context + thread
        var contextPath = "PartitionTestOrg";
        await NodeFactory.CreateNode(
            new MeshNode(contextPath) { Name = "Partition Test", NodeType = "Markdown" });

        var client = GetClient();
        var threadResponse = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Test partition storage")), o => o.WithTarget(new Address(contextPath))).FirstAsync().ToTask();
        threadResponse.Message.Success.Should().BeTrue(threadResponse.Message.Error);
        var threadPath = threadResponse.Message.Node!.Path;

        // Store plan
        await NodeFactory.CreateNode(new MeshNode("Plan", threadPath)
        {
            Name = "Test Plan",
            NodeType = "Markdown",
            Content = "# Simple plan\n1. Do thing A\n2. Do thing B"
        });

        // Verify the plan path contains _Thread (it's in the thread satellite partition)
        var planPath = $"{threadPath}/Plan";
        planPath.Should().Contain($"/{ThreadNodeType.ThreadPartition}/",
            "the plan lives inside the thread partition since its parent is a thread node");

        // Verify it's queryable by namespace
        var results = await MeshQuery
            .QueryAsync<MeshNode>($"namespace:{threadPath} nodeType:Markdown")
            .ToListAsync(ct);

        results.Should().ContainSingle(n => n.Id == "Plan",
            "plan should appear as a child of the thread node");
    }

    [Fact]
    public async Task StorePlan_CanUpdateExistingPlan()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create context + thread
        var contextPath = "UpdatePlanOrg";
        await NodeFactory.CreateNode(
            new MeshNode(contextPath) { Name = "Update Plan Test", NodeType = "Markdown" });

        var client = GetClient();
        var threadResponse = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Update plan test")), o => o.WithTarget(new Address(contextPath))).FirstAsync().ToTask();
        threadResponse.Message.Success.Should().BeTrue(threadResponse.Message.Error);
        var threadPath = threadResponse.Message.Node!.Path;

        // Create initial plan
        await NodeFactory.CreateNode(new MeshNode("Plan", threadPath)
        {
            Name = "Execution Plan",
            NodeType = "Markdown",
            Content = "# Plan v1\n1. Step one"
        });

        // Update the plan
        await NodeFactory.UpdateNode(new MeshNode("Plan", threadPath)
        {
            Name = "Execution Plan (revised)",
            NodeType = "Markdown",
            Content = "# Plan v2\n1. Step one (revised)\n2. Step two (added)"
        });

        // Verify updated content
        var planPath = $"{threadPath}/Plan";
        var node = await ReadNodeAsync(planPath, ct);

        node.Should().NotBeNull();
        node!.Name.Should().Be("Execution Plan (revised)");
        node.Content?.ToString().Should().Contain("Plan v2");
    }
}
