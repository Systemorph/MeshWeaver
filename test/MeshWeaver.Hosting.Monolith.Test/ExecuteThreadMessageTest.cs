using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests the full ExecuteThreadMessageRequest handler flow:
/// Client creates thread → posts ExecuteThreadMessageRequest → handler creates
/// user message node + response message node → agent streams response.
/// </summary>
public class ExecuteThreadMessageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponseText = "This is a test response from the fake agent.";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .WithTypes(typeof(ExecuteThreadMessageResponse));

    [Fact]
    public async Task ExecuteThreadMessage_CreatesUserAndResponseNodes()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;

        // 1. Create thread node (simulates ThreadChatView.AutoCreateThreadAsync)
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"User/TestUser/{threadId}";
        await NodeFactory.CreateNodeAsync(new MeshNode(threadPath)
        {
            Name = "Test Chat Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread { ParentPath = "User/TestUser" }
        }, ct);

        // 2. Post ExecuteThreadMessageRequest (fire-and-forget, like the Blazor client does)
        var client = GetClient();
        client.Post(
            new ExecuteThreadMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Hello, can you help me?"
            },
            o => o.WithTarget(new Address(threadPath)));

        // 3. Poll for child message nodes (handler runs asynchronously)
        List<MeshNode> children = [];
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct);
            children = await MeshQuery.QueryAsync<MeshNode>(
                $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"
            ).ToListAsync(ct);
            Output.WriteLine($"Poll {i}: {children.Count} children found");
            if (children.Count >= 2)
                break;
        }

        children.Should().HaveCountGreaterThanOrEqualTo(2,
            "should have user message + agent response");

        var messages = children
            .Select(n => n.Content)
            .OfType<ThreadMessage>()
            .ToList();

        // User message
        var userMsg = messages.FirstOrDefault(m => m.Role == "user");
        userMsg.Should().NotBeNull("should contain the user message");
        userMsg!.Text.Should().Be("Hello, can you help me?");
        userMsg.Type.Should().Be(ThreadMessageType.ExecutedInput);

        // Agent response
        var assistantMsg = messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMsg.Should().NotBeNull("should contain the agent response");
        assistantMsg!.Text.Should().NotBeEmpty("agent should have generated a response");
        assistantMsg.Type.Should().Be(ThreadMessageType.AgentResponse);
    }

    [Fact]
    public async Task ExecuteThreadMessage_SecondMessage_IncrementsMessageNumbers()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        // Create thread
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"User/TestUser/{threadId}";
        await NodeFactory.CreateNodeAsync(new MeshNode(threadPath)
        {
            Name = "Multi-Message Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread { ParentPath = "User/TestUser" }
        }, ct);

        var client = GetClient();

        // First message
        var response1 = await client.AwaitResponse(
            new ExecuteThreadMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "First question"
            },
            o => o.WithTarget(new Address(threadPath)),
            ct);
        response1.Message.Success.Should().BeTrue(response1.Message.Error);

        // Second message
        var response2 = await client.AwaitResponse(
            new ExecuteThreadMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Follow-up question"
            },
            o => o.WithTarget(new Address(threadPath)),
            ct);
        response2.Message.Success.Should().BeTrue(response2.Message.Error);

        // Verify 4 message nodes (2 user + 2 assistant)
        var children = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"
        ).ToListAsync(ct);

        children.Should().HaveCount(4, "should have 2 user messages + 2 agent responses");

        var ordered = children.OrderBy(n => n.Order).ToList();
        ordered[0].Order.Should().Be(1);
        ordered[1].Order.Should().Be(2);
        ordered[2].Order.Should().Be(3);
        ordered[3].Order.Should().Be(4);
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
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
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
            var agent = new ChatClientAgent(
                chatClient: new FakeChatClient(FakeResponseText),
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
}
