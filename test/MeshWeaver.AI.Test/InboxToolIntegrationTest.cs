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

    // â”€â”€â”€ check_inbox via the AIFunction surface â”€â”€â”€

    [Fact]
    public async Task CheckInbox_NoPending_ReturnsNoNewMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var result = await tool.InvokeAsync(new AIFunctionArguments(), ct);

        result.Should().NotBeNull();
        result!.ToString().Should().Be("(no new messages)");
    }

    [Fact]
    public async Task CheckInbox_OnePending_ReturnsItAndDrainsTheQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);

        // Atomically enter a genuine mid-execution state WITH the message queued,
        // gated on the tool's own stream (see SeedPendingMidExecutionAsync).
        var msgId = (await SeedPendingMidExecutionAsync(threadHub, ct, "hello mid-stream"))[0];

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var result = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();

        result.Should().Contain("hello mid-stream");
        result.Should().Contain("follow-up");

        // Verify state: PendingUserMessages drained, msgId added to IngestedMessageIds.
        var afterDrain = await WaitForOwnAsync(threadHub,
            t => t.IngestedMessageIds.Contains(msgId)
                 && !t.PendingUserMessages.ContainsKey(msgId),
            15_000, ct);

        afterDrain.PendingUserMessages.Should().NotContainKey(msgId);
        afterDrain.IngestedMessageIds.Should().Contain(msgId);
        // IsExecuting unchanged â€” drain doesn't flip lifecycle flags.
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
        // mid-stream" â€” the real flow has the agent already executing when
        // follow-ups arrive, and check_inbox is the only path that promotes
        // them out of PendingUserMessages.
        // Atomically enter mid-execution WITH all three queued (one own-stream write).
        var ids = await SeedPendingMidExecutionAsync(threadHub, ct, "first", "second", "third");

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var result = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();

        result.Should().Contain("3 follow-up messages");
        var firstIdx = result!.IndexOf("first", StringComparison.Ordinal);
        var secondIdx = result.IndexOf("second", StringComparison.Ordinal);
        var thirdIdx = result.IndexOf("third", StringComparison.Ordinal);
        firstIdx.Should().BeGreaterThan(0);
        secondIdx.Should().BeGreaterThan(firstIdx, "second should follow first in order");
        thirdIdx.Should().BeGreaterThan(secondIdx, "third should follow second in order");

        var after = await WaitForOwnAsync(threadHub,
            t => t.IngestedMessageIds.Count >= 3 && t.PendingUserMessages.IsEmpty,
            15_000, ct);

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

        // Atomically enter mid-execution WITH the message queued (one own-stream write).
        var msgId = (await SeedPendingMidExecutionAsync(threadHub, ct, "only one"))[0];

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var first = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        first.Should().Contain("only one");

        // Wait on the OWN stream (the tool's drain commit lands there) before the
        // second poll — the workspace round-trip is fire-and-forget inside the tool.
        await WaitForOwnAsync(threadHub,
            t => t.IngestedMessageIds.Contains(msgId), 15_000, ct);

        var second = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        second.Should().Be("(no new messages)",
            "once a message has been delivered to the agent it must NOT be redelivered on the next call");
    }

    // â”€â”€â”€ A7: clean mid-execution output-cell transition â”€â”€â”€

    /// <summary>
    /// A7 — while a round is streaming into response cell R1, a follow-up message
    /// arrives and the agent calls <c>check_inbox</c>. The clean output-cell
    /// transition must freeze R1 (Completed), place the interrupting user cell
    /// after it, and switch streaming to a FRESH cell R2 — final ordering
    /// <c>[R1 completed] → [U] → [R2 streaming]</c>. The agent's continuation
    /// streams into R2, NOT the frozen R1.
    /// </summary>
    [Fact]
    public async Task CheckInbox_DrainMidExecution_FreezesR1_InputsInMiddle_SwitchesToNewOutputCell()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);
        var client = GetClient();
        var ws = Mesh.GetWorkspace();

        // Round 1 — the fake client streams after a 5 s delay, so the round stays
        // Executing long enough to drive a mid-stream check_inbox.
        client.SubmitMessage(threadPath, "First question",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");

        var executing = await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && !string.IsNullOrEmpty(t.ActiveMessageId), 10_000, ct);
        var r1 = executing.ActiveMessageId!;

        // Wait until R1 is actually streaming — by then the round's
        // ActiveResponseSegment.ResponseText is wired, so check_inbox will split.
        await ws.GetMeshNodeStream($"{threadPath}/{r1}")
            .Select(n => (n?.Content as ThreadMessage)?.Text)
            .Where(txt => !string.IsNullOrEmpty(txt) && txt!.Contains("Generating response"))
            .Take(1).Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);

        // Queue a follow-up while round 1 streams.
        client.SubmitMessage(threadPath, "Follow-up while you work",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        var queued = await WaitForThreadAsync(threadPath,
            t => t.PendingUserMessages.Count > 0, 5_000, ct);
        var u2 = queued.PendingUserMessages.Keys.Single();

        // Mid-execution check_inbox → A7 clean output-cell transition.
        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var toolResult = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        toolResult.Should().Contain("Follow-up");

        // ActiveMessageId switched to a NEW response cell; the follow-up drained.
        var afterSplit = await WaitForThreadAsync(threadPath,
            t => !string.IsNullOrEmpty(t.ActiveMessageId)
                 && t.ActiveMessageId != r1
                 && t.IngestedMessageIds.Contains(u2),
            10_000, ct);
        var r2 = afterSplit.ActiveMessageId!;
        r2.Should().NotBe(r1, "check_inbox mid-execution must switch streaming to a fresh response cell");

        // Ordering: [r1 … u2 … r2] — the interrupting user cell in the middle.
        var idxR1 = afterSplit.Messages.IndexOf(r1);
        var idxU2 = afterSplit.Messages.IndexOf(u2);
        var idxR2 = afterSplit.Messages.IndexOf(r2);
        idxR1.Should().BeGreaterThanOrEqualTo(0);
        idxU2.Should().BeGreaterThan(idxR1, "the interrupting user message lands AFTER the frozen response cell");
        idxR2.Should().BeGreaterThan(idxU2, "the new response cell lands AFTER the user message");

        // R1 is frozen to a terminal Completed status (never regresses).
        var r1Status = await ws.GetMeshNodeStream($"{threadPath}/{r1}")
            .Select(n => (n?.Content as ThreadMessage)?.Status)
            .Where(s => s == ThreadMessageStatus.Completed)
            .Take(1).Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);
        r1Status.Should().Be(ThreadMessageStatus.Completed,
            "the in-flight cell is frozen Completed when check_inbox splits");

        // The agent's continuation streams into R2 (NOT the frozen R1) and the
        // round completes there.
        var r2Final = await ws.GetMeshNodeStream($"{threadPath}/{r2}")
            .Select(n => n?.Content as ThreadMessage)
            .Where(m => m is { Status: ThreadMessageStatus.Completed })
            .Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);
        r2Final!.Text.Should().Contain("slow ack",
            "the continuation streams into the NEW cell, not the frozen one");
    }

    // â”€â”€â”€ Cancel-then-restart: ESC with pending messages â”€â”€â”€

    [Fact]
    public async Task Cancel_WithPendingMessages_DispatchesNextRoundAfterCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);
        var client = GetClient();

        // Round 1: submit u1 with the slow model so we have a window to interrupt.
        client.SubmitMessage(
            threadPath, "First",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");

        var roundOneStart = await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count >= 1,
            10_000, ct);
        roundOneStart.IsExecuting.Should().BeTrue();
        var u1 = roundOneStart.IngestedMessageIds[0];

        // While round 1 is running, queue u2.
        client.SubmitMessage(
            threadPath, "Second",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");

        // Wait until u2 is queued (in PendingUserMessages but NOT yet ingested).
        var queuedState = await WaitForThreadAsync(threadPath,
            t => t.PendingUserMessages.Count >= 1
                 && t.IsExecuting,
            5_000, ct);
        queuedState.PendingUserMessages.Should().NotBeEmpty(
            "u2 must be queued in PendingUserMessages while round 1 runs");

        // ESC: post MeshThread.RequestedCancellationAt flip.
        // Cancel via stream.Update (see RequestViaStreamUpdate.md). Awaiting
        // the post-update emission asserts the write actually landed.
        var cancelled = await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .FirstAsync().ToTask(ct);
        (cancelled.Content as MeshThread)?.RequestedStatus.Should().Be(ThreadExecutionStatus.Cancelled);

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

        client.SubmitMessage(
            threadPath, "Lonely",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");

        var roundStart = await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count == 1,
            10_000, ct);
        var u1 = roundStart.IngestedMessageIds[0];
        var initialMsgsCount = roundStart.Messages.Count;

        // Cancel â€” no follow-ups queued.
        // Cancel via stream.Update (see RequestViaStreamUpdate.md). Awaiting
        // the post-update emission asserts the write actually landed.
        var cancelled = await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .FirstAsync().ToTask(ct);
        (cancelled.Content as MeshThread)?.RequestedStatus.Should().Be(ThreadExecutionStatus.Cancelled);

        // Wait for the cancel cleanup to settle (IsExecuting flips false).
        var afterCancel = await WaitForThreadAsync(threadPath,
            t => !t.IsExecuting, 10_000, ct);

        // Give the watcher ~1.5s of grace to incorrectly fire a phantom round.
        await Task.Delay(1500, ct);

        var final = await ReadThreadAsync(threadPath, ct);
        final.IsExecuting.Should().BeFalse(
            "no pending messages â†’ no phantom round should start");
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
        client.SubmitMessage(
            threadPath, "First",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count == 1, 10_000, ct);

        // Queue three follow-ups.
        foreach (var text in new[] { "Second", "Third", "Fourth" })
        {
            client.SubmitMessage(
                threadPath, text,
                modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        }
        await WaitForThreadAsync(threadPath,
            t => t.UserMessageIds.Count == 4, 5_000, ct);

        // ESC.
        // Cancel via stream.Update (see RequestViaStreamUpdate.md). Awaiting
        // the post-update emission asserts the write actually landed.
        var cancelled = await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .FirstAsync().ToTask(ct);
        (cancelled.Content as MeshThread)?.RequestedStatus.Should().Be(ThreadExecutionStatus.Cancelled);

        // All four should eventually end up ingested as the watcher dispatches
        // round 2/3/4 (one user message per round per PlanNextRound design).
        var final = await WaitForThreadAsync(threadPath,
            t => t.IngestedMessageIds.Count == 4 && !t.IsExecuting,
            60_000, ct);
        final.IngestedMessageIds.Should().HaveCount(4);
        final.UserMessageIds.Should().HaveCount(4);
        final.PendingUserMessages.Should().BeEmpty();
    }

    // â”€â”€â”€ ViewModel projection â”€â”€â”€

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

    // â”€â”€â”€ Helpers â”€â”€â”€

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
    /// <see cref="InboxTool.CheckInbox"/> reads the OWN node from the
    /// hub's workspace.
    /// </summary>
    private async Task<IMessageHub> GetThreadHubAsync(string threadPath, CancellationToken ct)
    {
        // Trigger hub activation by reading the node once via the workspace.
        await ReadNode(threadPath).FirstAsync().ToTask(ct);
        // Resolve the hub through the mesh service.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        // GetHub is internal â€” we use the same trick as ThreadExecution: the
        // routed hub becomes accessible by sending a no-op delivery and
        // reading back the workspace. Easier: spin up a hosted hub at the
        // address.
        var meshHub = Mesh.ServiceProvider.GetRequiredService<IMessageHub>();
        return meshHub.GetHostedHub(new Address(threadPath), config => config, HostedHubCreation.Always)!;
    }

    private async Task<MeshThread> ReadThreadAsync(string threadPath, CancellationToken ct)
    {
        var node = await ReadNode(threadPath).FirstAsync().ToTask(ct);
        node.Should().NotBeNull();
        var content = node!.Content as MeshThread;
        content.Should().NotBeNull();
        return content!;
    }

    /// <summary>
    /// Stream-based wait: subscribes to the thread's MeshNode stream and
    /// returns the first emission whose content matches <paramref name="predicate"/>.
    /// Replaces the previous Task.Delay polling loop — that pattern reads a
    /// potentially stale cached snapshot each poll cycle and races the
    /// workspace's write propagation. The stream emits on every commit, so
    /// the predicate sees every state transition exactly once.
    /// See CLAUDE.md → "Never Task.Delay to wait for propagation".
    /// </summary>
    private async Task<MeshThread> WaitForThreadAsync(
        string threadPath, Func<MeshThread, bool> predicate, int timeoutMs, CancellationToken ct)
    {
        MeshThread? last = null;
        try
        {
            return (await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
                .Select(n => n.Content as MeshThread)
                .Where(t => t is not null)
                .Do(t => last = t)
                .Where(t => predicate(t!))
                .Take(1)
                .Timeout(TimeSpan.FromMilliseconds(timeoutMs))
                .ToTask(ct))!;
        }
        catch (TimeoutException)
        {
            last.Should().NotBeNull(
                $"thread {threadPath} never emitted a MeshThread snapshot within {timeoutMs}ms");
            predicate(last!).Should().BeTrue(
                $"condition not reached within {timeoutMs}ms for thread {threadPath}. " +
                $"Last state: PendingUserMessages.Count={last!.PendingUserMessages.Count}, " +
                $"IngestedMessageIds=[{string.Join(",", last.IngestedMessageIds)}], " +
                $"IsExecuting={last.IsExecuting}");
            return last!;
        }
    }

    /// <summary>
    /// Stream-based wait on the thread hub's OWN node stream — the EXACT stream
    /// <see cref="InboxTool.CheckInbox"/> reads (<c>threadHub.GetWorkspace().GetMeshNodeStream()</c>).
    /// The mesh-side <see cref="WaitForThreadAsync"/> uses a separate remote stream
    /// with its own replay buffer, so a condition satisfied there isn't guaranteed
    /// visible on the tool's own stream yet. Gating the tool call on the OWN stream
    /// means the tool reads the same snapshot the wait just observed — closing the
    /// window where the submission watcher could drain between a mesh-side wait and
    /// the tool's own-stream read.
    /// </summary>
    private static async Task<MeshThread> WaitForOwnAsync(
        IMessageHub threadHub, Func<MeshThread, bool> predicate, int timeoutMs, CancellationToken ct)
    {
        MeshThread? last = null;
        try
        {
            return (await threadHub.GetWorkspace().GetMeshNodeStream()
                .Select(n => n.Content as MeshThread)
                .Where(t => t is not null)
                .Do(t => last = t)
                .Where(t => predicate(t!))
                .Take(1)
                .Timeout(TimeSpan.FromMilliseconds(timeoutMs))
                .ToTask(ct))!;
        }
        catch (TimeoutException)
        {
            predicate(last!).Should().BeTrue(
                $"own-stream condition not reached within {timeoutMs}ms for {threadHub.Address.Path}. " +
                $"Last: PendingUserMessages.Count={last?.PendingUserMessages.Count}, " +
                $"IngestedMessageIds=[{(last is null ? "" : string.Join(",", last.IngestedMessageIds))}], " +
                $"IsExecuting={last?.IsExecuting}");
            return last!;
        }
    }

    /// <summary>
    /// Seeds a GENUINE mid-execution state in ONE atomic own-stream write and
    /// returns the queued ids. The write sets Status=Executing AND an
    /// <see cref="MeshThread.ActiveMessageId"/> + <see cref="MeshThread.PendingUserMessage"/>
    /// (the shape a real in-flight turn has) AND the queued
    /// <see cref="MeshThread.PendingUserMessages"/>.
    ///
    /// <para>Why all in one write, with the active-round fields:
    /// <list type="bullet">
    ///   <item>ONE write → the submission watcher never observes a transient
    ///     <c>Idle+pending</c> (the two-write set-Executing-then-append shape let
    ///     the append's stale snapshot clobber Status back to Idle).</item>
    ///   <item>ActiveMessageId + PendingUserMessage → <see cref="ThreadExecution"/>'s
    ///     <c>RecoverStaleExecutingThread</c> SKIPS this thread (its guard treats it
    ///     as a live round). Otherwise its async init-read can race the write, see a
    ///     "stale" Executing thread with no active round, RESET Status→Idle, and the
    ///     watcher then drains the queue out from under check_inbox — the flake. No
    ///     test-side <c>stream.Where</c> can prevent that server-side drain, so we
    ///     stop it at the source by not looking stale. (WatchForExecution is deleted,
    ///     so these fields trigger no auto-execute.)</item>
    /// </list></para>
    /// </summary>
    private async Task<IReadOnlyList<string>> SeedPendingMidExecutionAsync(
        IMessageHub threadHub, CancellationToken ct, params string[] texts)
    {
        var ids = texts.Select(_ => Guid.NewGuid().AsString()).ToArray();
        var activeMsgId = $"active-{Guid.NewGuid():N}";
        await threadHub.GetWorkspace().GetMeshNodeStream().Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            var userIds = t.UserMessageIds;
            var pending = t.PendingUserMessages;
            for (var i = 0; i < texts.Length; i++)
            {
                userIds = userIds.Add(ids[i]);
                pending = pending.SetItem(ids[i],
                    ThreadInput.CreateUserMessage(texts[i], createdBy: "rbuergi@systemorph.com"));
            }
            return node with
            {
                Content = t with
                {
                    Status = ThreadExecutionStatus.Executing,
                    ActiveMessageId = activeMsgId,
                    PendingUserMessage = texts[0],
                    UserMessageIds = userIds,
                    PendingUserMessages = pending
                }
            };
        }).Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);

        // Gate on the SAME own stream check_inbox reads (load-tolerant budget).
        await WaitForOwnAsync(threadHub,
            t => t.IsExecuting && ids.All(t.PendingUserMessages.ContainsKey), 15_000, ct);
        return ids;
    }

    // â”€â”€â”€ Fake chat client + factory â”€â”€â”€

    /// <summary>
    /// Slow fake that delays ~5 s in the streaming path so tests can race in
    /// follow-up submissions and ESC during the in-flight turn. 1.5 s wasn't
    /// enough headroom on slow CI runners â€” the in-flight round could finish
    /// before the test had time to submit the queued u2 + assert.
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
            await Task.Delay(5000, cancellationToken);
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
