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
/// Repros the production "chat response stuck on 'Generating response...'"
/// bug. Symptom: ExecuteMessageAsync runs, pushes the placeholder
/// "Generating response..." into the response cell, kicks off the streaming
/// Task.Run — and the cell text NEVER advances past that placeholder. After
/// the streaming loop completes, the post-loop block at lines 906-918 in
/// ThreadExecution.cs is supposed to push the final text (empty or otherwise),
/// flip <c>IsExecuting=false</c>, and clear streaming state.
///
/// <para>This test simulates the case by registering an <see cref="IChatClient"/>
/// that yields zero <see cref="ChatResponseUpdate"/>s (mirrors the production
/// pathology where the underlying API call returns nothing). Asserts the
/// response cell ends up with NON-placeholder text and the thread is no
/// longer executing within a sane timeout.</para>
/// </summary>
public class StuckOnGeneratingResponseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new EmptyChatClientFactory());
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

    [Fact]
    public async Task EmptyStreamingResponse_DoesNotLeaveResponseCellStuckOnPlaceholder()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = GetClient();

        // Build thread + first user message + response cell, then post a
        // SubmitMessageRequest — same shape DispatchRound produces in prod.
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "hello?", "Roland");
        var createDelivery = client.Post(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address))!;
        var createResponse = await client.Observe(createDelivery).FirstAsync().ToTask(ct);
        var createMsg = ((IMessageDelivery<CreateNodeResponse>)createResponse).Message;
        createMsg.Success.Should().BeTrue(createMsg.Error);
        var threadPath = createMsg.Node!.Path!;

        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];

        await client.Observe(new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage
            {
                Role = "user", Text = "hello?", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
            }
        }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

        await client.Observe(new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse,
            }
        }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

        var submitResponse = await client.Observe(new SubmitMessageRequest
        {
            ThreadPath = threadPath,
            UserMessageText = "hello?",
            UserMessageId = userMsgId,
            ResponseMessageId = responseMsgId,
        }, o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // Subscribe to the response cell and wait for its text to advance
        // beyond the placeholder. Watching the cell directly avoids depending
        // on Thread.IngestedMessageIds (which only the watcher sets — this
        // test posts SubmitMessageRequest manually and bypasses it).
        var workspace = client.GetWorkspace();
        var responseCellPath = $"{threadPath}/{responseMsgId}";
        var cellStream = workspace.GetRemoteStream<MeshNode>(new Address(responseCellPath))!;

        string? finalText = null;
        try
        {
            finalText = await cellStream
                .Select(nodes => nodes?.FirstOrDefault(n => n.Path == responseCellPath)?.Content as ThreadMessage)
                .Where(m => m != null && !string.IsNullOrEmpty(m.Text) && m.Text != "Generating response...")
                .Select(m => m!.Text)
                .FirstAsync()
                .Timeout(15.Seconds())
                .ToTask(ct);
        }
        catch (TimeoutException)
        {
            // Surface what state the cell is actually in.
            var lastCell = await cellStream
                .Select(nodes => nodes?.FirstOrDefault(n => n.Path == responseCellPath)?.Content as ThreadMessage)
                .Where(m => m != null)
                .FirstAsync()
                .Timeout(2.Seconds())
                .ToTask(CancellationToken.None);
            Output.WriteLine($"[REPRO] cell stuck — text=\"{lastCell?.Text}\" role={lastCell?.Role}");
            throw;
        }

        finalText.Should().NotBe("Generating response...",
            "after the streaming loop completes (even with zero updates), the cell text "
            + "must be replaced — leaving it at the placeholder is the canonical 'stuck' bug "
            + "the user sees as a chat thread that never produces a reply.");
    }

    #region Empty-yield IChatClient + factory

    /// <summary>
    /// IChatClient that yields ZERO updates. Mimics the production failure mode
    /// where the underlying API hangs / returns nothing (e.g., Anthropic
    /// streaming endpoint silently keeps the connection open with no content).
    /// </summary>
    private sealed class EmptyChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EmptyProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

        // 🚨 Mimics the production hang: never yields, never completes.
        // Production cause is the Anthropic streaming endpoint being
        // misconfigured / unreachable / throttled — the HTTP call parks
        // the await forever. Without an execution-side watchdog the
        // response cell stays at "Generating response..." indefinitely.
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Wait until cancelled; never yield. If cancellation fires,
            // throw to emulate a real HttpClient cancellation.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class EmptyChatClientFactory : IChatClientFactory
    {
        public string Name => "EmptyFactory";
        public IReadOnlyList<string> Models => ["empty-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            return new ChatClientAgent(
                chatClient: new EmptyChatClient(),
                instructions: config.Instructions ?? "Empty test assistant.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);
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
