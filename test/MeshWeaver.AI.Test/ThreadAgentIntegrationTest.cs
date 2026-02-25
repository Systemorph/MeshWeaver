#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
using MeshWeaver.ShortGuid;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// End-to-end integration test for the full thread → agent → response flow.
/// Uses a fake IChatClient to avoid real AI API calls while testing
/// the complete pipeline: thread creation, message persistence,
/// agent initialization, streaming response, and reply storage.
/// </summary>
public class ThreadAgentIntegrationTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
    private const string FakeResponseText = "This is a test response from the fake agent.";

    public ThreadAgentIntegrationTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    #region Fake Chat Client Infrastructure

    private class FakeChatClient : IChatClient
    {
        private readonly string response;

        public FakeChatClient(string response) => this.response = response;

        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var msg = new ChatMessage(ChatRole.Assistant, response);
            return Task.FromResult(new ChatResponse(msg));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in response.Split(' '))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Yield();
            }
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

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            var chatClient = new FakeChatClient(FakeResponseText);
            var agent = new ChatClientAgent(
                chatClient: chatClient,
                instructions: config.Instructions ?? "You are a helpful test assistant.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null
            );
            return Task.FromResult(agent);
        }
    }

    #endregion

    #region End-to-End Integration Tests

    /// <summary>
    /// Full end-to-end flow:
    /// 1. Create Thread via IMeshCatalog.CreateNodeAsync
    /// 2. Create user ThreadMessage as child node
    /// 3. Initialize AgentChatClient, choose context and agent
    /// 4. Send message via GetStreamingResponseAsync
    /// 5. Create reply ThreadMessage from streamed response
    /// 6. Verify thread contains both messages in order
    /// </summary>
    [Fact]
    public async Task FullFlow_CreateThread_SendMessage_StreamResponse_SaveReply()
    {
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var query = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var ct = TestContext.Current.CancellationToken;

        // 1. Create thread node under ACME/ProductLaunch
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"ACME/ProductLaunch/{threadId}";
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Integration Test Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new Thread { ParentPath = "ACME/ProductLaunch" }
        };
        await catalog.CreateNodeAsync(threadNode, "test-user", ct);

        // 2. Create user message as child node
        var messageId = Guid.NewGuid().AsString();
        var userMessage = new ThreadMessage
        {
            Id = messageId,
            Role = "user",
            Text = "What is the status of the product launch?",
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.ExecutedInput
        };
        await catalog.CreateNodeAsync(new MeshNode($"{threadPath}/{messageId}")
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = userMessage
        }, "test-user", ct);

        // 3. Initialize AgentChatClient with context
        var agentChat = new AgentChatClient(Mesh.ServiceProvider);
        await agentChat.InitializeAsync("ACME/ProductLaunch");

        MeshNode? contextNode = null;
        await foreach (var node in query.QueryAsync<MeshNode>("path:ACME/ProductLaunch scope:self", null, ct))
        {
            contextNode = node;
            break;
        }
        contextNode.Should().NotBeNull("ACME/ProductLaunch node should exist in test data");

        agentChat.SetContext(new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = contextNode
        });
        agentChat.SetThreadId(threadPath);

        // 4. Choose agent — first ordered agent is the best match for context
        var agents = await agentChat.GetOrderedAgentsAsync();
        agents.Should().NotBeEmpty("agents should be loaded from mesh test data");
        agentChat.SetSelectedAgent(agents[0].Name);

        // 5. Send message and collect streaming response
        var chatMessages = new ChatMessage[]
        {
            new(ChatRole.User, "What is the status of the product launch?")
        };

        var responseBuilder = new StringBuilder();
        await foreach (var update in agentChat.GetStreamingResponseAsync(chatMessages, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                responseBuilder.Append(update.Text);
        }

        var responseText = responseBuilder.ToString().Trim();
        responseText.Should().NotBeEmpty("agent should produce a streaming response");
        responseText.Should().Contain("test response", "response should come from the fake agent");

        // 6. Create agent reply as child ThreadMessage
        var replyId = Guid.NewGuid().AsString();
        var replyMessage = new ThreadMessage
        {
            Id = replyId,
            Role = "assistant",
            AuthorName = agents[0].Name,
            Text = responseText,
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.AgentResponse
        };
        await catalog.CreateNodeAsync(new MeshNode($"{threadPath}/{replyId}")
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = replyMessage
        }, "test-user", ct);

        // 7. Verify thread contains both messages
        var children = new List<MeshNode>();
        await foreach (var child in query.QueryAsync<MeshNode>(
            $"path:{threadPath} nodeType:{ThreadMessageNodeType.NodeType} scope:children", null, ct))
        {
            children.Add(child);
        }

        children.Should().HaveCount(2, "thread should have the user message and agent reply");

        var messages = children.Select(c => c.Content).OfType<ThreadMessage>().ToList();
        messages.Should().HaveCount(2);

        var userMsg = messages.FirstOrDefault(m => m.Role == "user");
        userMsg.Should().NotBeNull("thread should contain the user message");
        userMsg!.Text.Should().Be("What is the status of the product launch?");
        userMsg.Type.Should().Be(ThreadMessageType.ExecutedInput);

        var assistantMsg = messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMsg.Should().NotBeNull("thread should contain the agent reply");
        assistantMsg!.Text.Should().Contain("test response");
        assistantMsg.Type.Should().Be(ThreadMessageType.AgentResponse);
    }

    /// <summary>
    /// Tests the non-streaming response path with the same thread/message flow.
    /// </summary>
    [Fact]
    public async Task FullFlow_CreateThread_SendMessage_NonStreamingResponse()
    {
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var query = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var ct = TestContext.Current.CancellationToken;

        // Create thread
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"ACME/ProductLaunch/{threadId}";
        await catalog.CreateNodeAsync(new MeshNode(threadPath)
        {
            Name = "Non-Streaming Test Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new Thread { ParentPath = "ACME/ProductLaunch" }
        }, "test-user", ct);

        // Initialize agent
        var agentChat = new AgentChatClient(Mesh.ServiceProvider);
        await agentChat.InitializeAsync("ACME/ProductLaunch");

        MeshNode? contextNode = null;
        await foreach (var node in query.QueryAsync<MeshNode>("path:ACME/ProductLaunch scope:self", null, ct))
        {
            contextNode = node;
            break;
        }

        agentChat.SetContext(new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = contextNode
        });
        agentChat.SetThreadId(threadPath);

        var agents = await agentChat.GetOrderedAgentsAsync();
        agents.Should().NotBeEmpty();
        agentChat.SetSelectedAgent(agents[0].Name);

        // Send via non-streaming path
        var chatMessages = new ChatMessage[]
        {
            new(ChatRole.User, "Tell me about the project")
        };

        var responseMessages = new List<ChatMessage>();
        await foreach (var msg in agentChat.GetResponseAsync(chatMessages, ct))
        {
            responseMessages.Add(msg);
        }

        responseMessages.Should().NotBeEmpty("agent should return at least one response message");

        var assistantMessages = responseMessages
            .Where(m => m.Role == ChatRole.Assistant)
            .ToList();
        assistantMessages.Should().NotBeEmpty("should have at least one assistant message");
    }

    /// <summary>
    /// Tests that switching thread IDs isolates conversation state.
    /// </summary>
    [Fact]
    public async Task SwitchThread_IsolatesConversationState()
    {
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var query = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var ct = TestContext.Current.CancellationToken;

        // Create two threads
        var threadId1 = Guid.NewGuid().AsString();
        var threadId2 = Guid.NewGuid().AsString();
        var threadPath1 = $"ACME/ProductLaunch/{threadId1}";
        var threadPath2 = $"ACME/ProductLaunch/{threadId2}";

        await catalog.CreateNodeAsync(new MeshNode(threadPath1)
        {
            Name = "Thread 1",
            NodeType = ThreadNodeType.NodeType,
            Content = new Thread { ParentPath = "ACME/ProductLaunch" }
        }, "test-user", ct);

        await catalog.CreateNodeAsync(new MeshNode(threadPath2)
        {
            Name = "Thread 2",
            NodeType = ThreadNodeType.NodeType,
            Content = new Thread { ParentPath = "ACME/ProductLaunch" }
        }, "test-user", ct);

        // Initialize agent
        var agentChat = new AgentChatClient(Mesh.ServiceProvider);
        await agentChat.InitializeAsync("ACME/ProductLaunch");

        MeshNode? contextNode = null;
        await foreach (var node in query.QueryAsync<MeshNode>("path:ACME/ProductLaunch scope:self", null, ct))
        {
            contextNode = node;
            break;
        }

        agentChat.SetContext(new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = contextNode
        });

        var agents = await agentChat.GetOrderedAgentsAsync();
        agentChat.SetSelectedAgent(agents[0].Name);

        // Send message on thread 1
        agentChat.SetThreadId(threadPath1);
        var response1 = new StringBuilder();
        await foreach (var update in agentChat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Message for thread 1")], ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                response1.Append(update.Text);
        }

        // Send message on thread 2
        agentChat.SetThreadId(threadPath2);
        var response2 = new StringBuilder();
        await foreach (var update in agentChat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Message for thread 2")], ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                response2.Append(update.Text);
        }

        // Both threads should produce responses
        response1.ToString().Trim().Should().NotBeEmpty();
        response2.ToString().Trim().Should().NotBeEmpty();

        // Persistence should have saved threads independently
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IChatPersistenceService>();
        var saved1 = await persistenceService.LoadThreadAsync(threadPath1, "shared");
        var saved2 = await persistenceService.LoadThreadAsync(threadPath2, "shared");
        saved1.Should().NotBeNull("thread 1 should be persisted");
        saved2.Should().NotBeNull("thread 2 should be persisted");
    }

    #endregion
}
