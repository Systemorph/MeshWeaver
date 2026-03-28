using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
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
/// Tests that reopening/resuming a thread loads all previously persisted messages.
/// Verifies the full round-trip: create thread → submit messages → reopen → messages are all there.
/// </summary>
public class ThreadResumeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponse = "This is the agent's response to verify resume.";
    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration)
            .AddData();
    }

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var response = await client.AwaitResponse(
            new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    private static ImmutableList<string> GetMessages(IEnumerable<MeshNode> nodes, string path)
    {
        var node = nodes?.FirstOrDefault(n => n.Path == path);
        return (node?.Content as MeshThread)?.Messages ?? ImmutableList<string>.Empty;
    }

    private async Task WaitForMessageCompleteAsync(string messagePath, CancellationToken ct)
    {
        // Derive thread path from message path (parent directory)
        var threadPath = messagePath[..messagePath.LastIndexOf('/')];
        for (var i = 0; i < 50; i++)
        {
            var threadNode = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync(ct);
            var msgNode = await MeshQuery.QueryAsync<MeshNode>($"path:{messagePath}").FirstOrDefaultAsync(ct);
            if (threadNode?.Content is MeshThread { IsExecuting: false } && msgNode?.Content is ThreadMessage { Text.Length: > 0 })
                return;
            await Task.Delay(200, ct);
        }
    }

    [Fact]
    public async Task Resume_ThreadWithMessages_LoadsAllMessages()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread and submit a message
        var threadPath = await CreateThreadAsync(client, "Resume test thread", ct);
        Output.WriteLine($"Thread: {threadPath}");

        var twoMessages = client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => GetMessages(nodes, threadPath))
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "First message for resume test",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Messages after first submit: [{string.Join(", ", msgIds)}]");

        // 2. Wait for response to complete
        await WaitForMessageCompleteAsync($"{threadPath}/{msgIds[1]}", ct);

        // 3. Verify messages are persisted in the database
        var threadNode = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync(ct);
        threadNode.Should().NotBeNull();
        var thread = threadNode!.Content as MeshThread;
        thread.Should().NotBeNull();
        thread!.Messages.Should().HaveCount(2, "thread should have 2 messages persisted");
        Output.WriteLine($"Persisted messages: [{string.Join(", ", thread.Messages)}]");

        // 4. SIMULATE RESUME: create a new client and subscribe to the same thread
        //    This mimics what happens when you navigate back to the thread URL
        var client2 = GetClient();
        var resumedMessages = client2.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => GetMessages(nodes, threadPath))
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var resumedMsgIds = await resumedMessages;
        resumedMsgIds.Should().HaveCount(2, "resumed thread should have all messages");
        resumedMsgIds[0].Should().Be(msgIds[0], "first message should match");
        resumedMsgIds[1].Should().Be(msgIds[1], "second message should match");
        Output.WriteLine($"Resumed messages: [{string.Join(", ", resumedMsgIds)}]");

        // 5. Verify each message content is accessible
        foreach (var msgId in resumedMsgIds)
        {
            var msgNode = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}/{msgId}").FirstOrDefaultAsync(ct);
            msgNode.Should().NotBeNull($"message {msgId} should be retrievable after resume");
            var tmsg = msgNode!.Content as ThreadMessage;
            tmsg.Should().NotBeNull($"message {msgId} should have ThreadMessage content");
            tmsg!.Text.Should().NotBeNullOrEmpty($"message {msgId} should have text");
            Output.WriteLine($"  {msgId}: role={tmsg.Role}, text='{tmsg.Text[..Math.Min(50, tmsg.Text.Length)]}'");
        }

        Output.WriteLine("Resume verified: all messages loaded correctly");
    }

    [Fact]
    public async Task Resume_ThreadWithMultipleExchanges_LoadsAll()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        // Create thread
        var threadPath = await CreateThreadAsync(client, "Multi-exchange resume test", ct);

        // Submit first message
        var twoMessages = client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => GetMessages(nodes, threadPath))
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "First question",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);

        var firstMsgIds = await twoMessages;
        await WaitForMessageCompleteAsync($"{threadPath}/{firstMsgIds[1]}", ct);

        // Submit second message
        var fourMessages = client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => GetMessages(nodes, threadPath))
            .Where(ids => ids.Count >= 4).FirstAsync().ToTask(ct);

        await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Second question",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);

        var allMsgIds = await fourMessages;
        allMsgIds.Should().HaveCount(4, "should have 2 exchanges = 4 messages");
        await WaitForMessageCompleteAsync($"{threadPath}/{allMsgIds[3]}", ct);

        // Resume: new client subscribes
        var client2 = GetClient();
        var resumedMessages = client2.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => GetMessages(nodes, threadPath))
            .Where(ids => ids.Count >= 4).FirstAsync().ToTask(ct);

        var resumed = await resumedMessages;
        resumed.Should().HaveCount(4, "resumed thread should have all 4 messages");
        resumed.Should().BeEquivalentTo(allMsgIds, o => o.WithStrictOrdering());

        Output.WriteLine($"Resumed {resumed.Count} messages across 2 exchanges");
    }

    #region Fake LLM

    private class FakeChatClient(string response) : IChatClient
    {
        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in response.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(10, cancellationToken);
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
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            var agent = new ChatClientAgent(
                chatClient: new FakeChatClient(FakeResponse),
                instructions: config.Instructions ?? "You are a test assistant.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);
            return Task.FromResult(agent);
        }
    }

    #endregion
}
