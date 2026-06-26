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
    public async Task CheckInbox_OnePending_DeliversInline_DoesNotWriteNodeMidRound()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);

        // Atomically enter a genuine mid-execution state WITH the message queued.
        // The submission watcher's OfferFromNode buffers the pending message into the
        // in-memory inbox channel (Stage 1) — no node write. (Gated on the own stream
        // showing the pending message, so OfferFromNode has run before we drain.)
        var msgId = (await SeedPendingMidExecutionAsync(threadHub, ct, "hello mid-stream"))[0];

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var result = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();

        // Stage 2: check_inbox drains the channel and delivers the text inline.
        result.Should().Contain("hello mid-stream");
        result.Should().Contain("💬", "in-flight messages are returned with the user-input marker (no boilerplate framing)");

        // 🚨 The core invariant: check_inbox does NOT write the thread node mid-round.
        // Pending is NOT cleared and the id is NOT ingested by the drain — that folding
        // happens only at the round's terminal boundary (no real round here, so it never
        // happens). The message is delivered purely from the in-memory channel.
        var afterDrain = await WaitForOwnAsync(threadHub, t => t.IsExecuting, 15_000, ct);
        afterDrain.PendingUserMessages.Should().ContainKey(msgId,
            "check_inbox must not clear PendingUserMessages mid-round — the fold happens at round-end");
        afterDrain.IngestedMessageIds.Should().NotContain(msgId,
            "check_inbox must not write IngestedMessageIds mid-round");
        afterDrain.IsExecuting.Should().BeTrue();

        // Second call: the channel was drained and the id was channeled, so it is NOT
        // re-delivered even though it is still in PendingUserMessages on the node.
        var second = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        second.Should().Be("(no new messages)",
            "once delivered from the channel a message must not be re-delivered (channeled guard)");
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

        result.Should().Contain("💬", "in-flight messages are returned with the user-input marker");
        var firstIdx = result!.IndexOf("first", StringComparison.Ordinal);
        var secondIdx = result.IndexOf("second", StringComparison.Ordinal);
        var thirdIdx = result.IndexOf("third", StringComparison.Ordinal);
        firstIdx.Should().BeGreaterThan(0);
        secondIdx.Should().BeGreaterThan(firstIdx, "second should follow first in order");
        thirdIdx.Should().BeGreaterThan(secondIdx, "third should follow second in order");

        // 🚨 check_inbox delivers all three inline from the in-memory channel WITHOUT
        // writing the node: they stay pending and un-ingested until the round-end fold
        // (no real round here). Drained-in-order is the contract; node mutation is not.
        var after = await WaitForOwnAsync(threadHub, t => t.IsExecuting, 15_000, ct);
        foreach (var id in ids)
        {
            after.PendingUserMessages.Should().ContainKey(id,
                "check_inbox must not clear pending mid-round — the fold happens at round-end");
            after.IngestedMessageIds.Should().NotContain(id,
                "check_inbox must not write IngestedMessageIds mid-round");
        }

        // Second call: all three were drained + channeled, so nothing is re-delivered.
        var second = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        second.Should().Be("(no new messages)");
    }

    [Fact]
    public async Task CheckInbox_TwoCallsBackToBack_SecondReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);

        // Atomically enter mid-execution WITH the message queued (one own-stream write).
        await SeedPendingMidExecutionAsync(threadHub, ct, "only one");

        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var first = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        first.Should().Contain("only one");

        // The drain is purely in-memory now (no workspace round-trip, no node write) — the
        // first call already dequeued + channeled the message synchronously, so the second
        // call needs no propagation wait. The id is still in PendingUserMessages on the
        // node (folded only at round-end), but the channeled guard prevents re-delivery.
        var second = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        second.Should().Be("(no new messages)",
            "once a message has been delivered to the agent it must NOT be redelivered on the next call");
    }

    // â”€â”€â”€ Mid-execution: in-flight message delivered inline, NO output-cell split â”€â”€â”€

    /// <summary>
    /// While a round is streaming into response cell R1, a follow-up message arrives and the
    /// agent calls <c>check_inbox</c>. The redesigned tool drains the follow-up (pending →
    /// ingested) and delivers it Claude-Code-style — returned to the agent and appended inline
    /// to the SAME response cell with a marker — rather than freezing R1 and switching to a
    /// fresh cell. So no second response cell appears and the round completes on R1 itself
    /// (its final text carries the agent's continuation, not a frozen partial).
    /// </summary>
    [Fact]
    public async Task CheckInbox_DrainMidExecution_DeliversInline_NoCellSplit()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);
        var client = GetClient();
        var ws = Mesh.GetWorkspace();

        // Round 1 — the fake client streams after a delay, so the round stays Executing.
        client.SubmitMessage(threadPath, "First question",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");

        var executing = await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && !string.IsNullOrEmpty(t.ActiveMessageId), 10_000, ct);
        var r1 = executing.ActiveMessageId!;

        // Wait until R1 is actually streaming (ActiveResponseSegment.ResponseText wired).
        await ws.GetMeshNodeStream($"{threadPath}/{r1}")
            .Select(n => (n?.Content as ThreadMessage)?.Text)
            .Where(txt => !string.IsNullOrEmpty(txt) && txt!.Contains("Generating response"))
            .Take(1).Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);

        // Queue a follow-up while round 1 streams. Gate on the thread hub's OWN stream so
        // the submission watcher's OfferFromNode (Stage 1, subscribed to the same own
        // stream) has buffered it into the in-memory channel before we drain.
        client.SubmitMessage(threadPath, "Follow-up while you work",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        var queued = await WaitForOwnAsync(threadHub,
            t => t.PendingUserMessages.Count > 0, 5_000, ct);
        var u2 = queued.PendingUserMessages.Keys.Single();

        // Mid-execution check_inbox → in-memory drain + inline delivery (no split, no node write).
        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var toolResult = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        toolResult.Should().Contain("Follow-up", "the tool returns the in-flight message text to the agent");

        // 🚨 During the round (still Executing) the follow-up is STILL pending on the node —
        // check_inbox delivered it in-memory and did NOT write the thread node. The fold
        // (pending → ingested) happens only at the round-end terminal write.
        var stillExecuting = await WaitForOwnAsync(threadHub,
            t => t.IsExecuting && t.PendingUserMessages.ContainsKey(u2), 5_000, ct);
        stillExecuting.PendingUserMessages.Should().ContainKey(u2,
            "check_inbox must not clear pending mid-round");
        stillExecuting.IngestedMessageIds.Should().NotContain(u2,
            "the follow-up is ingested only at the round-end fold, not by check_inbox");

        // The round completes on R1 itself — no fresh cell was created (the old A7 split would
        // have frozen R1 with partial text and streamed "slow ack" into a separate R2).
        var r1Final = await ws.GetMeshNodeStream($"{threadPath}/{r1}")
            .Select(n => n?.Content as ThreadMessage)
            .Where(m => m is { Status: ThreadMessageStatus.Completed })
            .Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);
        r1Final!.Text.Should().Contain("slow ack",
            "the agent's continuation streams into the SAME cell and completes there (no split)");

        // Round-end fold: the consumed follow-up is now in IngestedMessageIds and pending is
        // empty — delivered inline, never lost, and never re-dispatched as a fresh round.
        var afterRound = await WaitForThreadAsync(threadPath,
            t => !t.IsExecuting && t.IngestedMessageIds.Contains(u2), 10_000, ct);
        afterRound.PendingUserMessages.Should().NotContainKey(u2,
            "the consumed follow-up is folded out of pending at round-end");
        afterRound.IngestedMessageIds.Should().Contain(u2);
    }

    /// <summary>
    /// Focused two-stage-channel guard. While a slow round streams, TWO follow-ups arrive
    /// and the agent calls <c>check_inbox</c>: both are delivered INLINE from the in-memory
    /// channel, the thread node is NOT written by the drain (pending stays non-empty while
    /// Executing — the indirect proof that check_inbox no longer mutates the node mid-round),
    /// and at round-end both are folded into <c>IngestedMessageIds</c> with pending empty and
    /// nothing lost.
    /// </summary>
    [Fact]
    public async Task CheckInbox_TwoFollowUps_DeliveredInline_FoldedAtRoundEnd_NoMidRoundWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        var threadHub = await GetThreadHubAsync(threadPath, ct);
        var client = GetClient();

        // Round 1 — slow fake keeps the round Executing (~5 s).
        client.SubmitMessage(threadPath, "First",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && !string.IsNullOrEmpty(t.ActiveMessageId), 10_000, ct);

        // Two follow-ups, submitted serially so each lands cleanly in PendingUserMessages
        // (avoids the separate concurrent-submit array-replace race — out of scope here).
        // Gate each on the thread hub's OWN stream so the submission watcher's OfferFromNode
        // (Stage 1) has buffered it into the channel.
        client.SubmitMessage(threadPath, "Follow-up ONE",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        var afterFirst = await WaitForOwnAsync(threadHub,
            t => t.IsExecuting && t.PendingUserMessages.Count >= 1, 5_000, ct);
        var u2 = afterFirst.PendingUserMessages.Keys.First();

        client.SubmitMessage(threadPath, "Follow-up TWO",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        var afterSecond = await WaitForOwnAsync(threadHub,
            t => t.IsExecuting && t.PendingUserMessages.Count >= 2, 5_000, ct);
        var bothPending = afterSecond.PendingUserMessages.Keys.ToHashSet();

        // check_inbox: both delivered inline from the channel.
        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var toolResult = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        toolResult.Should().Contain("Follow-up ONE");
        toolResult.Should().Contain("Follow-up TWO");
        toolResult.Should().Contain("💬");

        // 🚨 Indirect no-mid-round-write proof: while Status is still Executing and after
        // check_inbox returned the steering text, BOTH follow-ups are STILL in
        // PendingUserMessages on the node and neither is ingested — the drain wrote nothing.
        var midRound = await WaitForOwnAsync(threadHub,
            t => t.IsExecuting, 5_000, ct);
        foreach (var id in bothPending)
        {
            midRound.PendingUserMessages.Should().ContainKey(id,
                "check_inbox must not clear pending mid-round");
            midRound.IngestedMessageIds.Should().NotContain(id,
                "check_inbox must not ingest mid-round — folding is at round-end");
        }

        // Round-end fold: both follow-ups land in IngestedMessageIds, pending empty, none lost.
        var afterRound = await WaitForThreadAsync(threadPath,
            t => !t.IsExecuting
                 && bothPending.All(id => t.IngestedMessageIds.Contains(id)),
            20_000, ct);
        afterRound.PendingUserMessages.Should().BeEmpty(
            "both consumed follow-ups are folded out of pending at round-end");
        afterRound.IngestedMessageIds.Should().Contain(u2);
        foreach (var id in bothPending)
            afterRound.IngestedMessageIds.Should().Contain(id, "no follow-up may be lost");
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
        // Self-diagnosing assert: this has failed CI-only (passes solo AND
        // in-class locally, 11/11). On the next hit the message names the
        // claimer's product - Status=StartingExecution means a stuck
        // submission-watcher claim (then: what re-populated pending?),
        // Status=Executing means a real round dispatched after the cancel.
        final.IsExecuting.Should().BeFalse(
            "no pending messages -> no phantom round should start. " +
            $"Status={final.Status}, ActiveMessageId={final.ActiveMessageId}, " +
            $"pending=[{string.Join(",", final.PendingUserMessages.Keys)}], " +
            $"ingested=[{string.Join(",", final.IngestedMessageIds)}], " +
            $"userIds=[{string.Join(",", final.UserMessageIds)}], " +
            $"messages={final.Messages.Count}, requested={final.RequestedStatus}");
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

    /// <summary>
    /// HAMMER: flood a thread with a burst of submits while round 1 is still streaming
    /// (the slow fake holds it ~5 s) so every follow-up lands in
    /// <see cref="MeshThread.PendingUserMessages"/> under contention, then drains across
    /// subsequent rounds. The invariant: <b>every submitted message is eventually ingested
    /// and rendered — none is lost.</b>
    ///
    /// <para>Regression guard for the message-loss flake behind the CI reds
    /// <see cref="Cancel_WithMultiplePending_RestartsAndDrainsAllInSubsequentRounds"/> and the
    /// Orleans <c>RapidSubmits_PileUpAndAllIngest</c>. The ROOT CAUSE was the cross-hub patch
    /// handler <c>DataExtensions.ApplyJsonMergePatchAndUpdate</c> being a NON-ATOMIC
    /// read-modify-write: it read a <c>stream.Take(1)</c> snapshot at handler time, JSON-merged
    /// the patch, then committed the full merged node via a SEPARATE deferred <c>RequestChange</c>
    /// turn. Two patches queued back-to-back at the owner each read pre-the-other's-write state,
    /// and the later full-node write overwrote a sibling writer's just-added field — dropping a
    /// queued message entirely. FIXED in <c>DataExtensions.ApplyMeshNodePatchAtomic</c>: the merge
    /// now applies onto the LIVE node in ONE primary-stream turn, serialised with every other
    /// writer. The deterministic repro is
    /// <c>CrossHubPatchAtomicityTest.ConcurrentCrossHubPatches_DoNotDropAQueuedMessage</c>; this
    /// hammer is the concurrency-level guard (the residual RFC 7396 array-replace under concurrent
    /// cross-mirror submits is reconciled from the merge-safe dict by
    /// <c>ThreadSubmissionServer.ReconcileUserMessageIds</c>).</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Hammer_ConcurrentSubmits_AllIngested_NoneLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadPath = await SeedEmptyThreadAsync(ct);
        // Activate the thread hub (and its submission watcher) before the burst.
        await GetThreadHubAsync(threadPath, ct);
        var client = GetClient();

        const int n = 12;
        var texts = Enumerable.Range(0, n).Select(i => $"hammer-{i:D2}").ToArray();

        // DIAGNOSTIC timeline: record every control-plane transition so a failure shows
        // whether the lost message ever reached PendingUserMessages and where it vanished.
        var timeline = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var recorder = Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(node => node?.Content as MeshThread)
            .Where(t => t is not null)
            .Subscribe(t => timeline.Enqueue(
                $"status={t!.Status} exec={t.IsExecuting} userIds={t.UserMessageIds.Count} "
                + $"ingested={t.IngestedMessageIds.Count} "
                + $"pending=[{string.Join(",", t.PendingUserMessages.Values.Select(m => m.Text).OrderBy(x => x))}] "
                + $"msgs={t.Messages.Count}"));

        // Round 1: submit the first and wait until it is genuinely executing
        // (the slow fake holds it ~5 s). This opens the pile-up window: the owner
        // is busy streaming while the burst lands.
        client.SubmitMessage(
            threadPath, texts[0],
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        await WaitForThreadAsync(threadPath,
            t => t.IsExecuting && t.IngestedMessageIds.Count >= 1, 15_000, ct);

        // HAMMER: fire the remaining submits back-to-back with NO await between them
        // (the real user-types-fast shape — same as RapidSubmits / Cancel_WithMultiplePending).
        // Each cross-mirror write diffs off the SAME stale owner-confirmed snapshot while
        // round 1 streams, so the non-atomic owner-side patch apply clobbers freshly-added
        // pending entries — the message-loss flake. Sequential on ONE thread (not Parallel.For)
        // so this repro matches the CI failure exactly and isn't conflated with the unrelated
        // non-synchronized-Subject race a multi-threaded burst would also trip.
        for (var i = 1; i < n; i++)
            client.SubmitMessage(
                threadPath, texts[i],
                modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");

        // Settle: thread idle, every submission ingested, nothing left pending.
        MeshThread final;
        try
        {
            final = await WaitForThreadAsync(threadPath,
                t => !t.IsExecuting
                     && t.IngestedMessageIds.Count >= n
                     && t.PendingUserMessages.IsEmpty,
                150_000, ct);
        }
        catch
        {
            FileOutput.WriteLine($"=== HAMMER TIMELINE ({timeline.Count} transitions) ===");
            foreach (var line in timeline) FileOutput.WriteLine(line);
            throw;
        }

        final.IngestedMessageIds.Should().HaveCount(n, "every submitted message must be ingested — none lost");
        final.UserMessageIds.Should().HaveCount(n, "no id may be clobbered out of UserMessageIds");
        final.PendingUserMessages.Should().BeEmpty();

        // Every submitted text survived as a rendered user cell (no silent loss).
        var ingestedTexts = await final.UserMessageIds
            .Select(id => ReadNode($"{threadPath}/{id}")
                .Select(node => (node!.Content as ThreadMessage)?.Text)
                .Where(text => !string.IsNullOrEmpty(text))
                .Take(1))
            .Merge()
            .ToList()
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);

        // Every submitted text round-trips into a user cell (none lost). The
        // assertions fork takes JsonSerializerOptions for element comparison.
        ingestedTexts.Should().BeEquivalentTo(texts, client.JsonSerializerOptions);
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
        // FromPath splits the namespace ("TestData/_Thread") from the id — `new MeshNode(path)`
        // would bake the slashes into the Id with an EMPTY namespace, which the
        // PartitionWriteGuard (correctly) rejects as a malformed top-level node.
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
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
    /// <c>check_inbox</c> reads the OWN node from the
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
                $"Last state: Status={last!.Status}, RequestedStatus={last.RequestedStatus}, " +
                $"ActiveMessageId={last.ActiveMessageId}, IsExecuting={last.IsExecuting}, " +
                $"pending=[{string.Join(",", last.PendingUserMessages.Keys)}], " +
                $"ingested=[{string.Join(",", last.IngestedMessageIds)}], " +
                $"userIds=[{string.Join(",", last.UserMessageIds)}], " +
                $"messages=[{string.Join(",", last.Messages)}]");
            return last!;
        }
    }

    /// <summary>
    /// Stream-based wait on the thread hub's OWN node stream — the EXACT stream
    /// <c>check_inbox</c> reads (<c>threadHub.GetWorkspace().GetMeshNodeStream()</c>).
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
    /// <see cref="MeshThread.ActiveMessageId"/>
    /// (the shape a real in-flight turn has) AND the queued
    /// <see cref="MeshThread.PendingUserMessages"/>.
    ///
    /// <para>Why all in one write, with the active-round fields:
    /// <list type="bullet">
    ///   <item>ONE write → the submission watcher never observes a transient
    ///     <c>Idle+pending</c> (the two-write set-Executing-then-append shape let
    ///     the append's stale snapshot clobber Status back to Idle).</item>
    ///   <item>ActiveMessageId → <see cref="ThreadExecution"/>'s
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
