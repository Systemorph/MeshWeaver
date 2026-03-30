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
using MeshWeaver.Layout;
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
/// End-to-end test: create a parent thread, then a delegation sub-thread under a
/// response message, submit a message to the sub-thread via SubmitMessageRequest,
/// and verify the full hierarchy is navigable with messages and tool calls.
/// </summary>
public class DelegationExecutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponse = "I found three relevant documents about the topic.";
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
            .AddLayoutClient();
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

    [Fact]
    public async Task DelegationSubThread_SubmitMessage_ProducesNavigableHierarchy()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        // 1. Create parent thread
        var threadPath = await CreateThreadAsync(client, "Delegation execution test", ct);
        Output.WriteLine($"Parent thread: {threadPath}");

        // 2. Submit a message to the parent thread (creates user + response messages)
        var parentMessages = client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes => GetMessages(nodes, threadPath))
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Research reinsurance pricing",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        var msgIds = await parentMessages;
        msgIds.Should().HaveCount(2);
        var responseMsgId = msgIds[1];
        Output.WriteLine($"Parent messages: [{string.Join(", ", msgIds)}], response={responseMsgId}");

        // 3. Wait for parent response to complete
        await WaitForMessageCompleteAsync(client, $"{threadPath}/{responseMsgId}", ct);

        // 4. Create delegation sub-thread under the response message
        var subThreadId = ThreadNodeType.GenerateSpeakingId("research reinsurance pricing");
        var parentMsgPath = $"{threadPath}/{responseMsgId}";
        var subThreadPath = $"{parentMsgPath}/{subThreadId}";

        await NodeFactory.CreateNodeAsync(new MeshNode(subThreadId, parentMsgPath)
        {
            Name = "Research reinsurance pricing",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread { ParentPath = threadPath }
        }, ct);
        Output.WriteLine($"Sub-thread created: {subThreadPath}");

        // 5. Submit message to the sub-thread via SubmitMessageRequest
        //    This goes through the full ThreadExecution pipeline.
        var subMessages = client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(subThreadPath))!
            .Select(nodes => GetMessages(nodes, subThreadPath))
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var subSubmitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = subThreadPath,
                UserMessageText = "Find documents about reinsurance pricing models",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(subThreadPath)), ct);
        subSubmitResponse.Message.Success.Should().BeTrue(subSubmitResponse.Message.Error);
        Output.WriteLine("Sub-thread message submitted");

        // 6. Wait for sub-thread messages
        var subMsgIds = await subMessages;
        subMsgIds.Should().HaveCount(2, "sub-thread should have user + response messages");
        Output.WriteLine($"Sub-thread messages: [{string.Join(", ", subMsgIds)}]");

        // 7. Wait for sub-thread response to complete
        var subResponsePath = $"{subThreadPath}/{subMsgIds[1]}";
        await WaitForMessageCompleteAsync(client, subResponsePath, ct);

        // 8. Verify full hierarchy is navigable

        // 8a. Parent thread has messages
        var parentThread = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync(ct);
        parentThread.Should().NotBeNull();
        var parentContent = parentThread!.Content as MeshThread;
        parentContent!.Messages.Should().HaveCount(2);

        // 8b. Sub-thread is findable under response message
        var subThreads = await MeshQuery
            .QueryAsync<MeshNode>($"namespace:{parentMsgPath} nodeType:{ThreadNodeType.NodeType}")
            .ToListAsync(ct);
        subThreads.Should().ContainSingle("should find exactly one sub-thread");
        subThreads[0].Path.Should().Be(subThreadPath);

        // 8c. Sub-thread has its own messages
        var subThread = await MeshQuery.QueryAsync<MeshNode>($"path:{subThreadPath}").FirstOrDefaultAsync(ct);
        var subContent = subThread!.Content as MeshThread;
        subContent!.Messages.Should().HaveCount(2);

        // 8d. Sub-thread user message has correct text
        var subUserMsg = await MeshQuery.QueryAsync<MeshNode>($"path:{subThreadPath}/{subMsgIds[0]}").FirstOrDefaultAsync(ct);
        var subUserContent = subUserMsg!.Content as ThreadMessage;
        subUserContent!.Role.Should().Be("user");
        subUserContent.Text.Should().Contain("reinsurance pricing");

        // 8e. Sub-thread response message has agent output
        var subRespMsg = await MeshQuery.QueryAsync<MeshNode>($"path:{subResponsePath}").FirstOrDefaultAsync(ct);
        var subRespContent = subRespMsg!.Content as ThreadMessage;
        subRespContent!.Role.Should().Be("assistant");
        subRespContent.Text.Should().NotBeNullOrEmpty("sub-agent should have produced a response");
        subContent.IsExecuting.Should().BeFalse("execution should be complete");

        Output.WriteLine($"Sub-thread response: '{subRespContent.Text}'");
        Output.WriteLine("Full hierarchy verified: parent → message → sub-thread → sub-messages");
    }

    private static ImmutableList<string> GetMessages(IEnumerable<MeshNode> nodes, string path)
    {
        var node = nodes?.FirstOrDefault(n => n.Path == path);
        return (node?.Content as MeshThread)?.Messages ?? ImmutableList<string>.Empty;
    }

    private async Task WaitForMessageCompleteAsync(IMessageHub client, string messagePath, CancellationToken ct)
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

    #region Fake LLM

    private class FakeChatClient : IChatClient
    {
        private readonly string response;
        public FakeChatClient(string response) => this.response = response;
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

        public ChatClientAgent CreateAgent(
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
            return agent;
        }

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
