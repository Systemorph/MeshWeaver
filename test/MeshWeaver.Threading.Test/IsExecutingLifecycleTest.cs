using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// 🚨 The repro for "chat stuck on Generating response..." — a single end-to-end
/// chat round that reactively asserts the full IsExecuting lifecycle:
/// <list type="number">
///   <item>flips to <c>true</c> shortly after SubmitMessageRequest (proves
///         execution actually started rather than the request being silently
///         dropped between hubs);</item>
///   <item>flips back to <c>false</c> when the agent finishes (proves the
///         streaming pipeline completed — not the watchdog forcing it
///         after 5min);</item>
///   <item>the final response cell text is the agent's real reply (not a
///         placeholder like "Allocating agent..." or "Generating response...").</item>
/// </list>
///
/// <para>The Echo agent ends in &lt;100ms; if anything in
/// <see cref="ThreadExecution.ExecuteMessageAsync"/> hangs (await foreach
/// deadlock, missing Subscribe on a cold observable, synced query waiting on
/// a blocked hub), the IsExecuting=false wait times out and the test fails
/// loud — instead of returning the placeholder text and silently green like
/// the old polling helper used to.</para>
/// </summary>
public class IsExecutingLifecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
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

    [Fact]
    public async Task SingleMessage_IsExecuting_FlipsTrueThenFalse_WithRealResponse()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();

        // Build the thread + cells exactly like the GUI flow
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "hello", "TestUser");
        var createDelivery = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        createDelivery.Message.Success.Should().BeTrue(createDelivery.Message.Error);
        var threadPath = createDelivery.Message.Node!.Path!;

        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];

        await client.Observe(new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage
            {
                Role = "user", Text = "hello", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

        await client.Observe(new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

        // Subscribe to the thread stream BEFORE posting the request so we
        // never miss the IsExecuting=true→false transition. Hot replay-1
        // semantics from GetMeshNodeStream guarantee the latest snapshot
        // arrives even if we're "late" — but starting the subscription
        // before the post is the safer pattern.
        var threadStream = Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t != null)
            .Replay(2);
        using var threadSub = threadStream.Connect();

        await client.Observe(new SubmitMessageRequest
        {
            ThreadPath = threadPath,
            UserMessageText = "hello",
            ContextPath = ContextPath,
            UserMessageId = userMsgId,
            ResponseMessageId = responseMsgId,
        }, o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

        // 1) IsExecuting must flip to true within ~10s. If this times out,
        //    SubmitMessageRequest reached the thread hub but the
        //    HandleSubmitMessage Update(Thread { IsExecuting = true })
        //    never propagated.
        var executingState = await threadStream
            .Where(t => t!.IsExecuting)
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);
        executingState!.IsExecuting.Should().BeTrue();
        executingState.ActiveMessageId.Should().Be(responseMsgId,
            "ActiveMessageId must point at the response cell during streaming");

        // 2) IsExecuting must flip BACK to false within 30s. If this times
        //    out, the Task.Run in ExecuteMessageAsync hung — most likely on
        //    `await foreach client.GetStreamingResponseAsync(...)` blocking
        //    forever (canonical IMeshService.QueryAsync deadlock pattern,
        //    or a missing Subscribe on a cold observable upstream).
        var doneState = await threadStream
            .Where(t => !t!.IsExecuting && t.Messages.Count >= 2)
            .Take(1)
            .Timeout(30.Seconds())
            .ToTask(ct);
        doneState!.IsExecuting.Should().BeFalse(
            "execution must terminate cleanly, not stay running until the watchdog");
        doneState.ExecutionStartedAt.Should().BeNull("started-at is cleared on completion");

        // 3) Response cell must hold the agent's REAL reply, not a placeholder.
        //    "Allocating agent..." / "Generating response..." both indicate
        //    the streaming pipeline never produced text. The Echo agent's
        //    reply contains the literal "I received N messages" string.
        var lastMsgId = doneState.Messages[^1];
        var responseStream = Mesh.GetWorkspace().GetMeshNodeStream($"{threadPath}/{lastMsgId}");
        var responseText = await responseStream
            .Select(n => (n.Content as ThreadMessage)?.Text)
            .Where(t => !string.IsNullOrEmpty(t)
                && !t.StartsWith("Allocating agent", StringComparison.Ordinal)
                && !t.StartsWith("Generating response", StringComparison.Ordinal)
                && !t.StartsWith("Loading conversation history", StringComparison.Ordinal))
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);

        responseText.Should().Contain("I received",
            "the Echo agent's streaming reply must reach the response cell — "
            + "if this fails with the placeholder, the streaming Task.Run hung "
            + "but the parent flipped IsExecuting=false anyway, masking a real bug.");
    }

    #region Echo IChatClient + factory (same shape as ChatHistoryTest)

    private sealed class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EchoProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"I received {messages.Count()} messages.")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var n = messages.Count();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                $"I received {n} messages in this conversation.");
            await Task.Delay(5, ct);
        }

        public object? GetService(Type serviceType, object? key = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class EchoChatClientFactory : IChatClientFactory
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
