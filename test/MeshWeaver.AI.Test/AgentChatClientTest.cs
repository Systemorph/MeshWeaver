#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for AgentChatClient using real mesh infrastructure.
/// Uses the samples/Graph/Data directory deployed to TestData.
/// </summary>
public class AgentChatClientTest : MonolithMeshTestBase
{
    // Path to the deployed test data (samples/Graph/Data copied to bin/TestData)
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public AgentChatClientTest(ITestOutputHelper output) : base(output)
    {
    }

    // Share Mesh/SP across [Fact]s.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            // AddAI registers Agent NodeType + AgentConfiguration content type so
            // .md files with `nodeType: Agent` deserialise into AgentConfiguration â€”
            // without this AgentPickerProjection.ProjectAgents filters them all out.
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Tests that AgentChatClient finds TodoAgent from ACME/Project namespace
    /// when navigating to ACME/ProductLaunch (which has NodeType="ACME/Project").
    ///
    /// Critical path verification:
    /// - ACME/ProductLaunch.json has nodeType="ACME/Project"
    /// - TodoAgent.md is located at ACME/Project/TodoAgent
    /// - Therefore TodoAgent should be found via the NodeType path, not via ancestors
    /// </summary>
    [Fact]
    public async Task AgentChatClient_InitializeAsync_FindsTodoAgentFromNodeTypeNamespace()
    {
        // Arrange - ACME/ProductLaunch has NodeType="ACME/Project", TodoAgent is at ACME/Project/TodoAgent
        var contextPath = "ACME/ProductLaunch";
        var expectedNodeType = "ACME/Project";
        var expectedTodoAgentPath = "ACME/Project/TodoAgent";

        // Static node read â€” no write before, catalog read is correct (no CQRS lag).
        var productLaunchNode = await MeshQuery.QueryAsync<MeshNode>($"path:{contextPath}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        productLaunchNode.Should().NotBeNull("ProductLaunch node should exist in test data");

        // CRITICAL: Verify the node has the expected NodeType
        productLaunchNode!.NodeType.Should().Be(expectedNodeType,
            "ProductLaunch node should have NodeType=ACME/Project to trigger NodeType-based agent search");

        // Act - Create AgentChatClient using the mesh's service provider
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);

        // 1. Initialize then SetContext â€” SetContext re-inits the subscription with
        //    the context node's NodeType as nodeTypePath, which is what brings in
        //    agents at namespace:{NodeType} via scope:selfAndAncestors.
        chatClient.Initialize(contextPath);
        chatClient.SetContext(new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = productLaunchNode
        });

        // Wait until the synced query emits with the NodeType-scoped agents.
        await chatClient.WhenInitialized
            .Where(c => c.GetOrderedAgentsAsync().Result.Any(a => a.Path == expectedTodoAgentPath))
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var orderedAgents = await chatClient.GetOrderedAgentsAsync();

        // Assert
        orderedAgents.Should().NotBeEmpty("Agents should be found from the mesh");

        // CRITICAL: TodoAgent should be FIRST because it has order: -10
        // (lower order = higher priority)
        var firstAgent = orderedAgents.First();
        firstAgent.Name.Should().Be("Todo Agent",
            "TodoAgent should be FIRST agent because it has order: -10 (lowest)");
        firstAgent.Path.Should().Be(expectedTodoAgentPath,
            "TodoAgent should come from the NodeType path (ACME/Project)");
    }

    /// <summary>
    /// Tests that when at a node with generic NodeType (Markdown),
    /// agents are still found from the path hierarchy.
    /// </summary>
    [Fact]
    public async Task AgentChatClient_InitializeAsync_FindsAgentsFromPathHierarchy()
    {
        // Arrange - Use a path that should have agents in hierarchy
        var contextPath = "ACME";

        // Static node read â€” no write before, catalog read is correct (no CQRS lag).
        var acmeNode = await MeshQuery.QueryAsync<MeshNode>($"path:{contextPath}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        acmeNode.Should().NotBeNull("ACME node should exist in test data");

        // Act
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        chatClient.Initialize(contextPath);
        chatClient.SetContext(new AgentContext
        {
            Address = new Address("ACME"),
            Node = acmeNode
        });

        // Wait for the synced query to emit a non-empty agent set.
        await chatClient.WhenInitialized
            .Where(c => c.GetOrderedAgentsAsync().Result.Count > 0)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var orderedAgents = await chatClient.GetOrderedAgentsAsync();

        // Assert - Should find agents from ACME hierarchy or root
        orderedAgents.Should().NotBeEmpty("Agents should be found from path hierarchy");
    }
}
