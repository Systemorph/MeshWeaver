using System;
using System.Collections.Generic;
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
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests for chat cancellation:
/// - Cancel stops streaming and marks message as cancelled
/// - Cancel propagates to sub-threads before cancelling own thread
/// </summary>
public class CancelThreadExecutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new SlowChatClientFactory());
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
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "Roland");
        var delivery = client.Post(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address))!;
        var response = await client.RegisterCallback(delivery, (d, _) => Task.FromResult(d), ct);
        var createResponse = ((IMessageDelivery<CreateNodeResponse>)response).Message;
        createResponse.Success.Should().BeTrue(createResponse.Error);
        return createResponse.Node!.Path!;
    }

    private IObservable<IReadOnlyList<string>> ObserveMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var content = node?.Content as MeshThread;
                return (IReadOnlyList<string>)(content?.Messages ?? []);
            });
    }

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        var nodeId = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
            o => o.WithTarget(new Address(path)), ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(Mesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(Mesh.JsonSerializerOptions);
        return null;
    }

    [Fact]
    public async Task CancelStream_StopsExecutionAndMarksAsCancelled()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread and submit (slow client will stream for ~5 seconds)
        var threadPath = await CreateThreadAsync(client, "Cancel test", ct);
        var twoMessages = ObserveMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Tell me a long story" },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Messages: [{string.Join(", ", msgIds)}]");

        // 2. Wait briefly for streaming to start
        await Task.Delay(500, ct);

        // Verify execution is in progress (check Thread.IsExecuting, not ThreadMessage)
        var midThread = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        midThread.Should().NotBeNull();
        midThread!.IsExecuting.Should().BeTrue("streaming should still be in progress");
        Output.WriteLine($"Mid-stream: Thread.IsExecuting={midThread.IsExecuting}");

        // 3. Cancel the stream
        Output.WriteLine("Sending CancelThreadStreamRequest...");
        client.Post(new CancelThreadStreamRequest { ThreadPath = threadPath },
            o => o.WithTarget(new Address(threadPath)));

        // 4. Wait for execution to stop (poll Thread.IsExecuting)
        MeshThread? finalThread = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(200, ct);
            finalThread = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
            if (finalThread is { IsExecuting: false })
                break;
        }

        // 5. Assert cancellation took effect
        finalThread.Should().NotBeNull();
        finalThread!.IsExecuting.Should().BeFalse("execution should be stopped after cancel");
        var finalContent = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}", ct);
        finalContent.Should().NotBeNull();
        finalContent!.Text.Should().Contain("Cancelled", "response should be marked as cancelled");
        Output.WriteLine($"Final: Thread.IsExecuting={finalThread.IsExecuting}, text='{finalContent.Text}'");

        // 6. Verify the fake client was actually interrupted (didn't stream all 50 words)
        var wordCount = finalContent.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        wordCount.Should().BeLessThan(50, "cancellation should have stopped streaming before all words were emitted");
        Output.WriteLine($"Word count: {wordCount} (expected < 50)");
    }

    #region Fake slow chat client

    /// <summary>
    /// Slow chat client that streams one word every 100ms, taking ~5 seconds total.
    /// Properly respects CancellationToken.
    /// </summary>
    private class SlowChatClient : IChatClient
    {
        private const string LongResponse =
            "Once upon a time in a land far away there lived a wise old wizard who knew many " +
            "secrets about the universe and spent his days reading ancient books in his tall " +
            "tower overlooking the vast ocean that stretched endlessly toward the horizon where " +
            "ships sailed carrying merchants and explorers seeking new worlds and adventures " +
            "beyond the known maps of their civilization and culture";

        public ChatClientMetadata Metadata => new("SlowProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, LongResponse)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in LongResponse.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(100, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class SlowChatClientFactory : IChatClientFactory
    {
        public string Name => "SlowFactory";
        public IReadOnlyList<string> Models => ["slow-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            var agent = new ChatClientAgent(
                chatClient: new SlowChatClient(),
                instructions: config.Instructions ?? "You are a slow test assistant.",
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
