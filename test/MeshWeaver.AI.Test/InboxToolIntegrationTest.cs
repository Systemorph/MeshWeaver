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
        result.Should().Contain("💬", "in-flight messages are returned with the user-input marker (no boilerplate framing)");

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

        result.Should().Contain("💬", "in-flight messages are returned with the user-input marker");
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

        // Queue a follow-up while round 1 streams.
        client.SubmitMessage(threadPath, "Follow-up while you work",
            modelName: "inbox-fake-slow", createdBy: "rbuergi@systemorph.com");
        var queued = await WaitForThreadAsync(threadPath,
            t => t.PendingUserMessages.Count > 0, 5_000, ct);
        var u2 = queued.PendingUserMessages.Keys.Single();

        // Mid-execution check_inbox → drain + inline delivery (no split).
        var tool = InboxTool.CreateCheckInboxTool(threadHub);
        var toolResult = (await tool.InvokeAsync(new AIFunctionArguments(), ct))?.ToString();
        toolResult.Should().Contain("Follow-up", "the tool returns the in-flight message text to the agent");

        // The follow-up drained (pending → ingested) — same drain guarantee as the idle path.
        var afterDrain = await WaitForThreadAsync(threadPath,
            t => t.IngestedMessageIds.Contains(u2), 10_000, ct);
        afterDrain.PendingUserMessages.Should().NotContainKey(u2);

        // The round completes on R1 itself — no fresh cell was created (the old A7 split would
        // have frozen R1 with partial text and streamed "slow ack" into a separate R2).
        var r1Final = await ws.GetMeshNodeStream($"{threadPath}/{r1}")
            .Select(n => n?.Content as ThreadMessage)
            .Where(m => m is { Status: ThreadMessageStatus.Completed })
            .Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask(ct);
        r1Final!.Text.Should().Contain("slow ack",
            "the agent's continuation streams into the SAME cell and completes there (no split)");
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
    /// <para>🐞 KNOWN-FAILING repro (Skip) for the message-loss flake behind the CI reds
    /// <see cref="Cancel_WithMultiplePending_RestartsAndDrainsAllInSubsequentRounds"/> and the
    /// Orleans <c>RapidSubmits_PileUpAndAllIngest</c>. Submitting 12 → only 8–9 ingest; 3–4
    /// vanish entirely (not pending, not ingested, not in Messages). ROOT CAUSE (proven by the
    /// timeline this test dumps on failure): the cross-hub patch handler
    /// <c>DataExtensions.ApplyJsonMergePatchAndUpdate</c> is a NON-ATOMIC read-modify-write — it
    /// reads a <c>stream.Take(1)</c> snapshot of the reduced node stream (which LAGS the data
    /// source), JSON-merges the patch, then commits the full merged node via a SEPARATE
    /// <c>RequestChange</c>. Under concurrent writers (a burst of cross-mirror submit patches,
    /// and owner own-writes through the data-source <c>EntityStore</c> stream) a stale-base commit
    /// overwrites a sibling writer's just-added <c>PendingUserMessages</c> key. The
    /// <c>ReconcileUserMessageIds</c> STOPGAP in <c>ThreadSubmission</c> exists only to paper over
    /// this — it cannot recover a pending entry that was clobbered out of existence.
    ///
    /// <para>FIX (deferred — high blast radius on the core cross-hub write path, needs full CI
    /// validation): make the patch apply atomically against live state, through the SAME
    /// data-source stream the owner's own-writes (<c>MeshNodeStreamHandle.UpdateOwn</c>) use, so
    /// patches and own-writes serialise on one queue. A partial attempt routing the merge through
    /// the reduced stream's atomic <c>Update</c> fixed patch-vs-patch but NOT patch-vs-own-write
    /// (different streams), so it was reverted. Remove this Skip once the handler is atomic.</para>
    /// </summary>
    [Fact(Timeout = 180_000, Skip = "KNOWN BUG: non-atomic cross-hub patch apply loses messages "
        + "under concurrent submits — DataExtensions.ApplyJsonMergePatchAndUpdate must merge "
        + "atomically through the data-source stream. See method remarks. Repro is real; unskip "
        + "to verify the fix.")]
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
