using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests that ALL previous messages are passed to the agent on each execution.
/// The echo agent reports how many ChatMessage objects it received.
/// Message 1: agent sees 1 msg. Message 2: agent sees 3 msgs. Message 3: agent sees 5 msgs.
/// History is loaded from ThreadMessage nodes via GetDataRequest, not from a cached field.
/// </summary>
public class ChatHistoryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new EchoChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var response = await client.AwaitResponse(
            new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    private async Task<string> SubmitAndWait(IMessageHub client, string threadPath, string text, int expectedMsgCount, CancellationToken ct)
    {
        // GUI flow: create cells first, then submit
        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];

        await client.AwaitResponse(new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage { Role = "user", Text = text, Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput }
        }), o => o.WithTarget(new Address(threadPath)), ct);

        await client.AwaitResponse(new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
        }), o => o.WithTarget(new Address(threadPath)), ct);

        await client.AwaitResponse(new SubmitMessageRequest
        {
            ThreadPath = threadPath, UserMessageText = text, ContextPath = ContextPath,
            UserMessageId = userMsgId, ResponseMessageId = responseMsgId
        }, o => o.WithTarget(new Address(threadPath)), ct);

        // Wait for execution to complete (Messages count reaches expected)
        for (var i = 0; i < 60; i++)
        {
            var node = await ReadNodeAsync(threadPath, ct);
            var thread = node?.Content as MeshThread;
            if (thread is { IsExecuting: false } && thread.Messages.Count >= expectedMsgCount)
            {
                // Read the last response message
                var lastMsgId = thread.Messages[^1];
                var msgNode = await ReadNodeAsync($"{threadPath}/{lastMsgId}", ct);
                var tmsg = msgNode?.Content as ThreadMessage;
                if (tmsg?.Text is { Length: > 0 })
                    return tmsg.Text;
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"Execution did not complete for {threadPath}");
    }

    [Fact]
    public async Task ThreeMessages_AgentSeesFullHistory()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();
        var threadPath = await CreateThreadAsync(client, "History test", ct);

        // Message 1: agent should see 1 message (just the user's)
        var response1 = await SubmitAndWait(client, threadPath, "First message", 2, ct);
        Output.WriteLine($"Response 1: {response1}");
        // ChatClientAgent adds system prompt as first message (+1 to all counts)
        response1.Should().Contain("2 messages", "first message: system + user");

        // Message 2: system + user1 + assistant1 + user2 = 4
        var response2 = await SubmitAndWait(client, threadPath, "Second message", 4, ct);
        Output.WriteLine($"Response 2: {response2}");
        response2.Should().Contain("4 messages", "second message: system + 2 history + 1 new");

        // Message 3: system + user1 + assistant1 + user2 + assistant2 + user3 = 6
        var response3 = await SubmitAndWait(client, threadPath, "Third message", 6, ct);
        Output.WriteLine($"Response 3: {response3}");
        response3.Should().Contain("6 messages", "third message: system + 4 history + 1 new");
    }

    [Fact]
    public async Task TwoMessages_NoDuplicates_CorrectRoles()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();
        var threadPath = await CreateThreadAsync(client, "Duplicate check", ct);

        // Message 1: "Hello"
        var response1 = await SubmitAndWait(client, threadPath, "Hello", 2, ct);
        Output.WriteLine($"Response 1: {response1}");
        // ChatClientAgent adds system prompt as first message
        response1.Should().Contain("2 messages", "first call: system prompt + user message");

        // Message 2: "World"
        var response2 = await SubmitAndWait(client, threadPath, "World", 4, ct);
        Output.WriteLine($"Response 2: {response2}");

        // Agent should see 4 messages: system + Hello + assistant-response + World
        response2.Should().Contain("4 messages",
            "second call: system + 2 history (user+assistant) + 1 new user = 4 total");
    }

    #region Echo LLM — responds with message count to verify history is passed

    private class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EchoProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default)
        {
            var count = messages.Count();
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"I received {count} messages in this conversation.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var msgList = messages.ToList();
            var summary = string.Join(" | ", msgList.Select((m, i) =>
                $"[{i}:{m.Role}:{(m.Text?.Length > 30 ? m.Text[..30] + "..." : m.Text)}]"));
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                $"I received {msgList.Count} messages in this conversation. Messages: {summary}");
            await Task.Delay(10, ct);
        }

        public object? GetService(Type serviceType, object? key = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class EchoChatClientFactory : IChatClientFactory
    {
        public string Name => "EchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new EchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
