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
        var response = await client.Observe(delivery).FirstAsync().ToTask(ct);
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
        var response = await client.Observe(new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)), o => o.WithTarget(new Address(path))).FirstAsync().ToTask(ct);
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
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread and submit (slow client streams one word per 100ms,
        //    ~5 seconds total — leaves ample window between "started" and "done"
        //    for the cancel to actually interrupt the loop).
        var threadPath = await CreateThreadAsync(client, "Cancel test", ct);

        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];

        await client.Observe(new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage { Role = "user", Text = "Tell me a long story", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput }
        }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

        await client.Observe(new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
        }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

        var submitResponse = await client.Observe(new SubmitMessageRequest
        {
            ThreadPath = threadPath, UserMessageText = "Tell me a long story",
            UserMessageId = userMsgId, ResponseMessageId = responseMsgId
        }, o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // 2. Open the streams BEFORE cancel so we never miss the emissions
        //    that race the cancel signal. Subscribe through the CLIENT hub's
        //    workspace (not Mesh.GetWorkspace()) — the client is what posted
        //    the SubmitMessageRequest and is the natural observer for the
        //    response cell, the same shape the GUI uses. Mesh.GetWorkspace()
        //    targets the mesh-routing hub and doesn't always activate the
        //    per-node-hub remote stream the same way a chat client does.
        var clientWorkspace = client.GetWorkspace();
        var threadStream = clientWorkspace.GetMeshNodeStream(threadPath);
        var responseStream = clientWorkspace.GetMeshNodeStream($"{threadPath}/{responseMsgId}");

        // 2a. Wait for the response cell to show "Generating response..." text.
        //     This is the deterministic "streaming-loop-is-armed" signal:
        //     in ThreadExecution.ExecuteMessageAsync, this PushToResponseMessage
        //     fires AFTER `parentHub.Set(executionCts)` and immediately before
        //     `Task.Run(streaming loop)`. So if we observe that text we know
        //     the CancellationTokenSource is already stored on the thread hub
        //     and HandleCancelStream's `hub.Get<CancellationTokenSource>()` will
        //     find it. Waiting only on `IsExecuting=true` (which flips earlier,
        //     in HandleSubmitMessage) races the CTS-set and the cancel handler
        //     was finding null → no-op → streaming completed naturally and the
        //     test's word-count assertion failed with all 64 words present.
        await responseStream
            .Select(n => (n.Content as ThreadMessage)?.Text ?? "")
            .Where(t => t.StartsWith("Generating response", StringComparison.Ordinal))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        Output.WriteLine("Streaming confirmed armed (response cell shows 'Generating response...')");

        // 3. Cancel the stream
        Output.WriteLine("Sending CancelThreadStreamRequest...");
        client.Post(new CancelThreadStreamRequest { ThreadPath = threadPath },
            o => o.WithTarget(new Address(threadPath)));

        // 4. Wait for execution to stop via the stream — IsExecuting=false.
        //    Replaces the old 30×200ms poll loop on GetDataRequest, which
        //    races per-node hub state and can hit the GetDataRequest hang
        //    if the loop catches the hub mid-Dispose.
        var settled = await threadStream
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        settled.Should().NotBeNull();
        Output.WriteLine($"Settled: Thread.IsExecuting={settled!.IsExecuting}");

        // 5. Read the response cell's settled content — wait for ANY non-empty
        //    text emission past the placeholders. ExecuteMessageAsync's
        //    OperationCanceledException branch writes "<accumulated>\n\n*Cancelled*",
        //    but if cancel raced past the catch block (very fast cancel before
        //    the streaming loop accumulated anything) the text might just be
        //    empty/placeholder — we still verify cancellation via the
        //    word-count assertion below, which is the authoritative signal
        //    that streaming was interrupted.
        var finalContent = await responseStream
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m?.Text is { Length: > 0 } txt
                && !txt.StartsWith("Allocating agent", StringComparison.Ordinal)
                && !txt.StartsWith("Generating response", StringComparison.Ordinal)
                && !txt.StartsWith("Loading conversation history", StringComparison.Ordinal))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        finalContent.Should().NotBeNull();
        Output.WriteLine($"Final response text: '{finalContent!.Text}'");

        // 6. Verify the fake client was actually interrupted (didn't stream all 50 words).
        //    This is the deterministic cancellation signal — IsExecuting=false alone
        //    would also be reached on natural completion of the slow client (~5s).
        var wordCount = finalContent.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        wordCount.Should().BeLessThan(50,
            "cancellation should have stopped streaming before all words were emitted");
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
