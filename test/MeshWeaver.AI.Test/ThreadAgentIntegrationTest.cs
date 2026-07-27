#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
/// End-to-end integration test for the full thread â†’ agent â†’ response flow.
/// Uses a fake IChatClient to avoid real AI API calls while testing
/// the complete pipeline: thread creation, message persistence,
/// agent initialization, streaming response, and reply storage.
/// </summary>
public class ThreadAgentIntegrationTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
    private const string FakeResponseText = "This is a test response from the fake agent.";

    // This class INSTALLS ITS OWN AGENT and selects it BY NAME. The picker's registry is the UNION
    // of the built-in catalog (BuiltInAgentProvider, shipped from content/ai/Agent) and whatever
    // has been persisted — and every AI.Test class shares ONE file-system persistence root
    // (AppContext.BaseDirectory/TestData — see the same constant in AgentChatClientTest,
    // AttachmentContextTest, …), which additionally survives between runs because it lives under
    // bin/. So `agents[0]` selected by POSITION out of a set whose membership and ordering depend
    // on class ordering, shard assignment and leftover on-disk state. Owning the agent and
    // selecting it by name makes the choice deterministic and any extra agent harmless.
    // Run-unique, and xUnit builds a fresh class instance per [Fact], so each test owns a distinct
    // agent. Both halves matter: the mesh is shared across this class's facts
    // (ShareMeshAcrossTests), and TestDataPath lives under bin/ so it also survives BETWEEN runs —
    // a fixed id collides with itself on the second fact AND on the second run
    // ("Node already exists"). ExportImportRoundTripTest documents the same run-unique-id need.
    private readonly string testAgentId = $"ThreadAgentIntegrationTestAgent-{Guid.NewGuid().AsString()}";
    private const string AgentContextPath = "ACME/ProductLaunch";

    public ThreadAgentIntegrationTest(ITestOutputHelper output) : base(output) { }

    // Share Mesh/SP across [Fact]s â€” see MonolithMeshTestBase.ShareMeshAcrossTests.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var assemblyRoot = Path.Combine(Path.GetTempPath(), "MeshWeaver.AI.Test", "assemblies",
            Guid.NewGuid().AsString());
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                services.AddFileSystemAssemblyStore(assemblyRoot);
                return services;
            })
            .AddGraph()
            .AddAI()
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

        public ChatClientAgent CreateAgent(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            var chatClient = new FakeChatClient(FakeResponseText);
            return new ChatClientAgent(
                chatClient: chatClient,
                instructions: config.Instructions ?? "You are a helpful test assistant.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null
            );
        }

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion

    #region Own-agent fixture

    /// <summary>
    /// Seeds this class's own agent into the namespace the picker actually queries
    /// (AgentPickerProjection.BuildAgentQuery → <c>namespace:ACME/Agent|…|Agent nodeType:Agent</c>).
    /// Called once per [Fact], against the run-unique <see cref="testAgentId"/> — the create is NOT
    /// idempotent (it throws "Node already exists"), which is why the id must not be a constant.
    /// </summary>
    private Task<MeshNode> SeedOwnAgent() =>
        SeedTopLevel(MeshNode.FromPath($"ACME/Agent/{testAgentId}") with
        {
            NodeType = AgentNodeType.NodeType,
            Name = testAgentId,
            Content = new AgentConfiguration
            {
                Id = testAgentId,
                Description = "Fixture agent owned by ThreadAgentIntegrationTest",
            },
        });

    /// <summary>
    /// Builds a chat client on the fixture context with THIS class's agent selected, and waits for
    /// that agent to actually be in the picker's set — NOT merely for
    /// <see cref="AgentChatClient.WhenInitialized"/> to fire once.
    ///
    /// That distinction is the second half of the flake. The synced agent query emits `Initial`
    /// first, and Initial-with-0-agents is a legitimate steady state that deliberately fires
    /// readiness (see the comment in <c>AgentChatClient.Initialize</c>: gating on count&gt;0 would
    /// hang forever when no agents are configured). So `WhenInitialized.FirstAsync()` returns
    /// before any agent has landed, and the very next line read an empty list. WhenInitialized
    /// re-emits on every refresh, so filtering for our agent is the real ready-gate.
    /// </summary>
    private async Task<AgentChatClient> StartChatWithOwnAgent(string threadPath, CancellationToken ct)
    {
        await SeedOwnAgent();

        var contextNode = await ReadNode(AgentContextPath).FirstAsync().ToTask(ct);
        contextNode.Should().NotBeNull($"{AgentContextPath} node should exist in test data");

        var agentChat = new AgentChatClient(Mesh.ServiceProvider);
        // Context BEFORE Initialize: Initialize defaults its NodeType-search namespace from
        // Context.Node.NodeType, so setting it first makes the query fully determined.
        agentChat.SetContext(new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = contextNode
        });
        agentChat.Initialize(AgentContextPath);

        var agents = await agentChat.WhenInitialized
            .SelectMany(c => Observable.FromAsync(c.GetOrderedAgentsAsync))
            .Where(a => a.Any(x => x.Name == testAgentId))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(ct);

        agents.Should().Contain(a => a.Name == testAgentId,
            "the test installs its own agent rather than depending on whatever the shared registry holds");

        agentChat.SetThreadId(threadPath);
        agentChat.SetSelectedAgent(testAgentId);
        return agentChat;
    }

    #endregion

    #region End-to-End Integration Tests

    /// <summary>
    /// Full end-to-end flow:
    /// 1. Create Thread via NodeFactory.CreateNode
    /// 2. Create user ThreadMessage as child node
    /// 3. Initialize AgentChatClient, choose context and agent
    /// 4. Send message via GetStreamingResponseAsync
    /// 5. Create reply ThreadMessage from streamed response
    /// 6. Verify thread contains both messages in order
    /// </summary>
    [Fact]
    public async Task FullFlow_CreateThread_SendMessage_StreamResponse_SaveReply()
    {
        var query = MeshQuery;
        var ct = TestContext.Current.CancellationToken;

        // 1. Create thread node under ACME/ProductLaunch
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"ACME/ProductLaunch/{threadId}";
        var threadNode = new MeshNode(threadPath)
        {
            Name = "Integration Test Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new Thread()
        };
        await NodeFactory.CreateNode(threadNode);

        // 2. Create user message as child node
        var messageId = Guid.NewGuid().AsString();
        var userMessage = new ThreadMessage
        {
            Role = "user",
            Text = "What is the status of the product launch?",
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.ExecutedInput
        };
        await NodeFactory.CreateNode(new MeshNode($"{threadPath}/{messageId}")
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = userMessage
        });

        // 3./4. Initialize AgentChatClient on the fixture context with THIS class's own agent
        // selected (see StartChatWithOwnAgent — no dependency on the shared ambient registry).
        var agentChat = await StartChatWithOwnAgent(threadPath, ct);

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
        responseText.Should().NotBeNullOrEmpty("agent should produce a streaming response");
        responseText.Should().Contain("test response", "response should come from the fake agent");

        // 6. Create agent reply as child ThreadMessage
        var replyId = Guid.NewGuid().AsString();
        var replyMessage = new ThreadMessage
        {
            Role = "assistant",
            AuthorName = testAgentId,
            Text = responseText,
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.AgentResponse
        };
        await NodeFactory.CreateNode(new MeshNode($"{threadPath}/{replyId}")
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = replyMessage
        });

        // 7. Verify thread contains both messages
        var children = new List<MeshNode>();
        await foreach (var child in query.QueryAsync<MeshNode>(
            $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}", null, ct))
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
        var query = MeshQuery;
        var ct = TestContext.Current.CancellationToken;

        // Create thread
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"ACME/ProductLaunch/{threadId}";
        await NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = "Non-Streaming Test Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new Thread()
        });

        // Initialize agent — this class's own, not the shared ambient registry. The context-node
        // null-check that used to live here now sits in StartChatWithOwnAgent, so both flows keep
        // it: without it a stalled read (a silent null) let the whole flow run against a NULL
        // context and still pass every assertion below — the failure only appeared 60 s later as
        // a dispose-time watchdog blaming the CancellationToken.
        var agentChat = await StartChatWithOwnAgent(threadPath, ct);

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
        var query = MeshQuery;
        var ct = TestContext.Current.CancellationToken;

        // Create two threads
        var threadId1 = Guid.NewGuid().AsString();
        var threadId2 = Guid.NewGuid().AsString();
        var threadPath1 = $"ACME/ProductLaunch/{threadId1}";
        var threadPath2 = $"ACME/ProductLaunch/{threadId2}";

        await NodeFactory.CreateNode(new MeshNode(threadPath1)
        {
            Name = "Thread 1",
            NodeType = ThreadNodeType.NodeType,
            Content = new Thread()
        });

        await NodeFactory.CreateNode(new MeshNode(threadPath2)
        {
            Name = "Thread 2",
            NodeType = ThreadNodeType.NodeType,
            Content = new Thread()
        });

        // Initialize agent — this class's own, not the shared ambient registry. This test used to
        // index agents[0] with no NotBeEmpty guard at all, so an empty ambient registry surfaced as
        // an IndexOutOfRangeException rather than a readable failure.
        var agentChat = await StartChatWithOwnAgent(threadPath1, ct);

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
        response1.ToString().Trim().Should().NotBeNullOrEmpty();
        response2.ToString().Trim().Should().NotBeNullOrEmpty();

        // Thread persistence is now via MeshNodes â€” no separate IChatPersistenceService
    }

    #endregion
}
