#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// End-to-end tests for the <c>check_inbox</c> tool: drains
/// <see cref="MeshThread.PendingUserMessages"/> mid-stream so the agent can
/// fold queued user input into the in-flight reply, and the
/// cancel-then-restart behavior that ensures pending messages still
/// dispatch a fresh round when the user hits ESC during a turn.
/// </summary>
public class InboxToolIntegrationTest : AITestBase
{
    public InboxToolIntegrationTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new InboxFakeChatClientFactory());
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    // ─── check_inbox via the AIFunction surface ───

    [Fact]
    public async Task CheckInbox_NoPending_ReturnsNoNewMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var result = await tool.InvokeAsync(new AIFunctionArguments(), ct);

        result.Should().NotBeNull();
        result.ToString().Should().Be("(no new messages)");
    }

    [Fact]
    public async Task CheckInbox_OnePending_ReturnsItAndDrainsTheQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);

        // Lock IsExecuting=true BEFORE appending so the server-side watcher
        // doesn't drain the queue first. Simulates mid-stream agent state.
        await SetIsExecutingAsync(threadHub, true, ct);

        var msgId = ThreadInput.AppendUserInput(
            threadHub.GetWorkspace(), threadPath,
            ThreadInput.CreateUserMessage("hello mid-stream", createdBy: "rbuergi@systemorph.com"));
        await WaitForThreadAsync(threadPath,
            t => t.PendingUserMessages.ContainsKey(msgId) && t.IsExecuting, 5_000, ct);

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var result = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();

        result.Should().Contain("hello mid-stream");
        result.Should().Contain("follow-up");

        // Verify state: PendingUserMessages drained, msgId added to IngestedMessageIds.
        var afterDrain = await WaitForThreadAsync(threadPath,
            t => t.IngestedMessageIds.Contains(msgId)
                 && !t.PendingUserMessages.ContainsKey(msgId),
            5_000, ct);

        afterDrain.PendingUserMessages.Should().NotContainKey(msgId);
        afterDrain.IngestedMessageIds.Should().Contain(msgId);
        // IsExecuting unchanged — drain doesn't flip lifecycle flags.
        afterDrain.IsExecuting.Should().BeTrue();
    }

    [Fact]
    public async Task CheckInbox_MultiplePending_ReturnsAllInOrder_AndDrains()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);

        // Lock IsExecuting=true BEFORE appending so the server-side watcher
        // doesn't drain the queue during our setup. We're simulating "agent is
        // mid-stream" — the real flow has the agent already executing when
        // follow-ups arrive, and check_inbox is the only path that promotes
        // them out of PendingUserMessages.
        await SetIsExecutingAsync(threadHub, true, ct);

        var workspace = threadHub.GetWorkspace();
        var ids = new List<string>();
        foreach (var text in new[] { "first", "second", "third" })
        {
            var id = ThreadInput.AppendUserInput(
                workspace, threadPath,
                ThreadInput.CreateUserMessage(text, createdBy: "rbuergi@systemorph.com"));
            ids.Add(id);
        }
        await WaitForThreadAsync(threadPath,
            t => t.PendingUserMessages.Count == 3 && t.IsExecuting, 5_000, ct);

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var result = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();

        result.Should().Contain("3 follow-up messages");
        var firstIdx = result!.IndexOf("first", StringComparison.Ordinal);
        var secondIdx = result.IndexOf("second", StringComparison.Ordinal);
        var thirdIdx = result.IndexOf("third", StringComparison.Ordinal);
        firstIdx.Should().BeGreaterThan(0);
        secondIdx.Should().BeGreaterThan(firstIdx, "second should follow first in order");
        thirdIdx.Should().BeGreaterThan(secondIdx, "third should follow second in order");

        var after = await WaitForThreadAsync(threadPath,
            t => t.IngestedMessageIds.Count >= 3 && t.PendingUserMessages.IsEmpty,
            5_000, ct);

        foreach (var id in ids)
            after.IngestedMessageIds.Should().Contain(id);
        after.PendingUserMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckInbox_TwoCallsBackToBack_SecondReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);

        // Lock IsExecuting=true first so the watcher doesn't drain the queue.
        await SetIsExecutingAsync(threadHub, true, ct);

        var msgId = ThreadInput.AppendUserInput(
            threadHub.GetWorkspace(), threadPath,
            ThreadInput.CreateUserMessage("only one", createdBy: "rbuergi@systemorph.com"));
        await WaitForThreadAsync(threadPath,
            t => t.PendingUserMessages.ContainsKey(msgId) && t.IsExecuting, 5_000, ct);

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var first = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        first.Should().Contain("only one");

        // Wait briefly for the drain UpdateMeshNode to commit before the second
        // poll (the workspace round-trip is fire-and-forget inside the tool).
        await WaitForThreadAsync(threadPath,
            t => t.IngestedMessageIds.Contains(msgId), 5_000, ct);

        var second = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        second.Should().Be("(no new messages)",
            "once a message has been delivered to the agent it must NOT be redelivered on the next call");
    }

    // ─── Cancel-then-restart: ESC with pending messages ───

    [Fact]
    public async Task Cancel_WithPendingMessages_DispatchesNextRoundAfterCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);
        var client = GetClient();

        // Round 1: submit u1 with the slow model so we have a window to interrupt.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "First",
            ModelName = "inbox-fake-slow", CreatedBy = "rbuergi@systemorph.com"
        });

        var roundOneStart = await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count >= 1,
            10_000, ct);
        roundOneStart.IsExecuting.Should().BeTrue();
        var u1 = roundOneStart.IngestedMessageIds[0];

        // While round 1 is running, queue u2.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "Second",
            ModelName = "inbox-fake-slow", CreatedBy = "rbuergi@systemorph.com"
        });

        // Wait until u2 is queued (in PendingUserMessages but NOT yet ingested).
        var queuedState = await WaitForThreadAsync(threadPath,
            t => t.PendingUserMessages.Count >= 1
                 && t.IsExecuting,
            5_000, ct);
        queuedState.PendingUserMessages.Should().NotBeEmpty(
            "u2 must be queued in PendingUserMessages while round 1 runs");

        // ESC: post CancelThreadStreamRequest.
        client.Post(new CancelThreadStreamRequest { ThreadPath = threadPath },
            o => o.WithTarget(new Address(threadPath)));

        // After cancel, the watcher should pick up u2 and dispatch round 2.
        // Round 2 ingests u2 and produces a NEW response cell distinct from r1.
        var afterCancel = await WaitForThreadAsync(threadPath,
            t => t.IngestedMessageIds.Count >= 2,
            20_000, ct);

        afterCancel.IngestedMessageIds.Count.Should().BeGreaterThanOrEqualTo(2,
            "u2 must be ingested into a new round after ESC drains the in-flight one");
        afterCancel.IngestedMessageIds.Should().Contain(u1);
        afterCancel.PendingUserMessages.Should().BeEmpty(
            "all queued messages should be drained by the time round 2 is dispatched");
    }

    [Fact]
    public async Task Cancel_NoPendingMessages_DoesNotDispatchAnotherRound()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);
        var client = GetClient();

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "Lonely",
            ModelName = "inbox-fake-slow", CreatedBy = "rbuergi@systemorph.com"
        });

        var roundStart = await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count == 1,
            10_000, ct);
        var u1 = roundStart.IngestedMessageIds[0];
        var initialMsgsCount = roundStart.Messages.Count;

        // Cancel — no follow-ups queued.
        client.Post(new CancelThreadStreamRequest { ThreadPath = threadPath },
            o => o.WithTarget(new Address(threadPath)));

        // Wait for the cancel cleanup to settle (IsExecuting flips false).
        var afterCancel = await WaitForThreadAsync(threadPath,
            t => !t.IsExecuting, 10_000, ct);

        // Give the watcher ~1.5s of grace to incorrectly fire a phantom round.
        await Task.Delay(1500, ct);

        var final = await ReadThreadAsync(threadPath, ct);
        final.IsExecuting.Should().BeFalse(
            "no pending messages → no phantom round should start");
        final.IngestedMessageIds.Should().ContainSingle().Which.Should().Be(u1);
        final.Messages.Count.Should().Be(initialMsgsCount,
            "no new cells expected since nothing was queued");
    }

    [Fact]
    public async Task Cancel_WithMultiplePending_RestartsAndDrainsAllInSubsequentRounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);
        var client = GetClient();

        // Round 1.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client, ThreadPath = threadPath, UserText = "First",
            ModelName = "inbox-fake-slow", CreatedBy = "rbuergi@systemorph.com"
        });
        await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count == 1, 10_000, ct);

        // Queue three follow-ups.
        foreach (var text in new[] { "Second", "Third", "Fourth" })
        {
            ThreadSubmission.Submit(new SubmitContext
            {
                Hub = client, ThreadPath = threadPath, UserText = text,
                ModelName = "inbox-fake-slow", CreatedBy = "rbuergi@systemorph.com"
            });
        }
        await WaitForThreadAsync(threadPath,
            t => t.UserMessageIds.Count == 4, 5_000, ct);

        // ESC.
        client.Post(new CancelThreadStreamRequest { ThreadPath = threadPath },
            o => o.WithTarget(new Address(threadPath)));

        // All four should eventually end up ingested as the watcher dispatches
        // round 2/3/4 (one user message per round per PlanNextRound design).
        var final = await WaitForThreadAsync(threadPath,
            t => t.IngestedMessageIds.Count == 4 && !t.IsExecuting,
            60_000, ct);
        final.IngestedMessageIds.Should().HaveCount(4);
        final.UserMessageIds.Should().HaveCount(4);
        final.PendingUserMessages.Should().BeEmpty();
    }

    // ─── ViewModel projection ───

    [Fact]
    public void ExtractPendingTexts_EmptyThread_ReturnsEmpty()
    {
        var thread = new MeshThread();
        var texts = ThreadLayoutAreas.ExtractPendingTexts(thread);
        texts.Should().BeEmpty();
    }

    [Fact]
    public void ExtractPendingTexts_TwoPending_ReturnsInUserMessageIdsOrder()
    {
        var m1 = new ThreadMessage { Role = "user", Text = "alpha" };
        var m2 = new ThreadMessage { Role = "user", Text = "beta" };
        var thread = new MeshThread
        {
            UserMessageIds = ImmutableList.Create("a", "b"),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("b", m2).Add("a", m1) // reversed insertion to prove ordering
        };

        var texts = ThreadLayoutAreas.ExtractPendingTexts(thread);

        texts.Should().ContainInOrder("alpha", "beta");
    }

    [Fact]
    public void ExtractPendingTexts_NullThread_ReturnsEmpty()
    {
        var texts = ThreadLayoutAreas.ExtractPendingTexts(null);
        texts.Should().BeEmpty();
    }

    // ─── Helpers ───

    private async Task<string> SeedEmptyThreadAsync(CancellationToken ct)
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = $"Inbox Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        });
        return threadPath;
    }

    /// <summary>
    /// Resolves the thread hub via the in-process MeshService. Required because
    /// <see cref="InboxTool.CheckInboxAsync"/> reads the OWN node from the
    /// hub's workspace.
    /// </summary>
    private async Task<IMessageHub> GetThreadHubAsync(string threadPath, CancellationToken ct)
    {
        // Trigger hub activation by reading the node once via the workspace.
        await ReadNodeAsync(threadPath, ct);
        // Resolve the hub through the mesh service.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        // GetHub is internal — we use the same trick as ThreadExecution: the
        // routed hub becomes accessible by sending a no-op delivery and
        // reading back the workspace. Easier: spin up a hosted hub at the
        // address.
        var meshHub = Mesh.ServiceProvider.GetRequiredService<IMessageHub>();
        return meshHub.GetHostedHub(new Address(threadPath), config => config, HostedHubCreation.Always)!;
    }

    private async Task<MeshThread> ReadThreadAsync(string threadPath, CancellationToken ct)
    {
        var node = await ReadNodeAsync(threadPath, ct);
        node.Should().NotBeNull();
        var content = node!.Content as MeshThread;
        content.Should().NotBeNull();
        return content!;
    }

    private async Task<MeshThread> WaitForThreadAsync(
        string threadPath, Func<MeshThread, bool> predicate, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        MeshThread? last = null;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            last = await ReadThreadAsync(threadPath, ct);
            if (predicate(last)) return last;
            await Task.Delay(100, ct);
        }
        last.Should().NotBeNull();
        predicate(last!).Should().BeTrue(
            $"condition not reached within {timeoutMs}ms for thread {threadPath}. " +
            $"Last state: PendingUserMessages.Count={last!.PendingUserMessages.Count}, " +
            $"IngestedMessageIds=[{string.Join(",", last.IngestedMessageIds)}], " +
            $"IsExecuting={last.IsExecuting}");
        return last!;
    }

    /// <summary>
    /// Sets <see cref="MeshThread.IsExecuting"/> on the thread node directly.
    /// Used by check_inbox tests to keep the watcher from racing the tool call.
    /// </summary>
    private async Task SetIsExecutingAsync(IMessageHub threadHub, bool isExecuting, CancellationToken ct)
    {
        var workspace = threadHub.GetWorkspace();
        await workspace.GetMeshNodeStream().Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = t with { IsExecuting = isExecuting } };
        }).Take(1).ToTask(ct);
    }

    // ─── Fake chat client + factory ───

    /// <summary>
    /// Slow fake that delays ~1.5 s in the streaming path so tests can race in
    /// follow-up submissions and ESC during the in-flight turn.
    /// </summary>
    private sealed class InboxFakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("InboxFakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ack")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(1500, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "slow ack");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class InboxFakeChatClientFactory : IChatClientFactory
    {
        public string Name => "InboxFakeFactory";
        public IReadOnlyList<string> Models => ["inbox-fake-slow"];
        public int Order => 0;

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(
                chatClient: new InboxFakeChatClient(),
                instructions: config.Instructions ?? "inbox test assistant",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null);

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }
}
