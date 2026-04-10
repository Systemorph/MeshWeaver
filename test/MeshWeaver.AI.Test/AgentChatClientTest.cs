#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    private class FakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fake")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "fake");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private class FakeChatClientFactory : IChatClientFactory
    {
        public string Name => "FakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new FakeChatClient(), instructions: config.Instructions ?? "", name: config.Id, description: config.Description ?? config.Id, tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    /// <summary>
    /// Tests that AgentChatClient loads agents when initialized with a context path.
    /// InitializeAsync queries agents from the context path's ancestor hierarchy
    /// and the global Agent namespace, then creates agent instances via the factory.
    /// </summary>
    [Fact]
    public async Task AgentChatClient_InitializeAsync_FindsAgentsFromContextAndGlobalNamespace()
    {
        // Arrange - ACME/ProductLaunch has NodeType="ACME/Project"
        var contextPath = "ACME/ProductLaunch";

        // Verify the node exists in test data
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        MeshNode? productLaunchNode = null;
        await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{contextPath}", null, TestContext.Current.CancellationToken))
        {
            productLaunchNode = node;
            break;
        }
        productLaunchNode.Should().NotBeNull("ProductLaunch node should exist in test data");

        // Act - Create AgentChatClient and initialize with context path
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync(contextPath);

        var context = new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = productLaunchNode
        };
        chatClient.SetContext(context);

        var orderedAgents = await chatClient.GetOrderedAgentsAsync();

        // Assert - agents should be loaded from global Agent namespace
        orderedAgents.Should().NotBeEmpty("Agents should be found from the mesh");
        orderedAgents.Should().Contain(a => a.Name == "Orchestrator",
            "Built-in Orchestrator agent should be loaded from the Agent namespace");
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
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        MeshNode? acmeNode = null;
        await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{contextPath}", null, TestContext.Current.CancellationToken))
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
