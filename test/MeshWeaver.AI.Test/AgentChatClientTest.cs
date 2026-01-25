#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                return services;
            })
            .AddJsonGraphConfiguration(TestDataPath)
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

        // Load the actual node from the file system
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        MeshNode? productLaunchNode = null;
        await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self"))
        {
            productLaunchNode = node;
            break;
        }
        productLaunchNode.Should().NotBeNull("ProductLaunch node should exist in test data");

        // CRITICAL: Verify the node has the expected NodeType
        productLaunchNode!.NodeType.Should().Be(expectedNodeType,
            "ProductLaunch node should have NodeType=ACME/Project to trigger NodeType-based agent search");

        // Act - Create AgentChatClient using the mesh's service provider
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);

        // 1. Initialize - this should load agents including TodoAgent from NodeType namespace
        await chatClient.InitializeAsync(contextPath);

        // 2. Set context with the actual node from file system
        var context = new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = productLaunchNode
        };
        chatClient.SetContext(context);

        // 3. Get ordered agents - this should return TodoAgent
        var orderedAgents = await chatClient.GetOrderedAgentsAsync();

        // Assert
        orderedAgents.Should().NotBeEmpty("Agents should be found from the mesh");

        // CRITICAL: TodoAgent should be FIRST because it has displayOrder: -10
        // (lower displayOrder = higher priority)
        var firstAgent = orderedAgents.First();
        firstAgent.Name.Should().Be("TodoAgent",
            "TodoAgent should be FIRST agent because it has displayOrder: -10 (lowest)");
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

        // Load the actual node from the file system
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        MeshNode? acmeNode = null;
        await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self"))
        {
            acmeNode = node;
            break;
        }
        acmeNode.Should().NotBeNull("ACME node should exist in test data");

        // Act
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync(contextPath);

        var context = new AgentContext
        {
            Address = new Address("ACME"),
            Node = acmeNode
        };
        chatClient.SetContext(context);

        var orderedAgents = await chatClient.GetOrderedAgentsAsync();

        // Assert - Should find agents from ACME hierarchy or root
        orderedAgents.Should().NotBeEmpty("Agents should be found from path hierarchy");
    }
}
