#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins token-usage accounting across EVERY terminal round outcome.
/// <para>
/// The agent stream emits <see cref="UsageContent"/>; <c>ThreadExecution</c> aggregates it and
/// must (a) stamp the per-message response cell's
/// <see cref="ThreadMessage.InputTokens"/>/<see cref="ThreadMessage.OutputTokens"/>/<see cref="ThreadMessage.TotalTokens"/>
/// and (b) accumulate the per-(thread, model) <see cref="TokenUsage"/> satellite at
/// {threadPath}/_Usage/{model} — on Completed, Cancelled, AND Error rounds alike.
/// </para>
/// <para>
/// These tests cover the four holes the accounting had: the round-dispatch reset that defeated
/// cumulative accumulation, the wake-cancel wipe, and the Cancelled/Error paths that dropped
/// usage entirely. They also pin the in+out → total derivation when a provider omits the total.
/// </para>
/// </summary>
public class ThreadTokenUsageTest : AITestBase
{
    /// <summary>
    /// The one wait budget in this class, and it is a HANG-STOPPER — not an assertion.
    ///
    /// <para>Every wait here is observable-driven and asserts a VALUE (<c>InputTokens == …</c>,
    /// <c>Status == Cancelled</c>); the millisecond figure only stops a broken run from hanging
    /// forever. That is the whole reason it may be generous: unlike the bounds in
    /// <see cref="ConcurrencyStressCollection"/>, which ARE the assertion (deadlock and
    /// lost-write detectors), padding this one weakens nothing — it cannot turn a wrong value
    /// into a right one.</para>
    ///
    /// <para>It exists because the file used to carry two budgets, 10 s and 20 s, split by no
    /// stated rule: thread-state waits got 20 s and satellite waits 10 s, on the assumption that a
    /// satellite lands promptly once the thread has settled. On a shared 4-vCPU CI runner it does
    /// not always, and #2001 is the resulting failure — a cancel that had already landed
    /// (<c>WaitForThread(Cancelled)</c> passed) followed by a usage satellite that had not
    /// appeared inside its tighter 10 s.</para>
    ///
    /// <para><b>This class deliberately does NOT join <see cref="ConcurrencyStressCollection"/>,</b>
    /// which was the other candidate fix. That collection's membership rule is structural and
    /// requires BOTH a test that creates concurrency of its own AND a verdict that is a wall-clock
    /// bound on the burst. This one does neither: it is a strictly sequential flow — seed, submit,
    /// wait, cancel, wait — and its verdicts are value comparisons. Adding it would turn a rule
    /// the collection's own documentation defends as "structural, not whatever failed last time"
    /// into exactly that, and would cost the shard wall clock for a test that does not need the
    /// box.</para>
    /// </summary>
    private const int SettleBudgetMs = 20_000;

    // Distinct in/out so a test can tell which field a value landed in (catches an in/out swap).
    private const int InTokens = 137;
    private const int OutTokens = 89;
    private const int TotalTokens = InTokens + OutTokens; // 226
    // Prompt-cache subset of InTokens (137 includes the 40 read + 25 write, per the UsageTokens
    // convention). Distinct so a test can tell read from write.
    private const int CacheReadTokens = 40;
    private const int CacheWriteTokens = 25;

    private const string TestUser = "rbuergi@systemorph.com";
    // Streamed AFTER the usage update — when this lands on the cell, the streaming loop has
    // provably pulled (and aggregated) the usage. The Cancelled test gates on it so the cancel
    // deterministically lands after token aggregation, with no sleep.
    private const string UsageMarker = "[usage-accounted]";

    // The lone text chunk a provider-reports-nothing client streams before blocking/throwing.
    // Deliberately longer than the framework's own initial "Generating response..." placeholder
    // (22 chars) — PushToResponseMessage's monotonic-growth guard keeps whichever of
    // current/incoming text is LONGER while Status is Streaming, so anything shorter would never
    // actually replace the placeholder on the cell (see the comment at the yield site).
    // No trailing whitespace: StripSummaryBlock (the streaming push's TrimEnd) would strip it
    // before the text reaches the cell, breaking an exact-suffix Contains check on the gate below.
    private const string NoReportWorkingText = "Working — no provider usage report on this stream.";

    public ThreadTokenUsageTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new UsageChatClientFactory());
                services.AddSingleton<IChatClientFactory>(new UsageNoTotalChatClientFactory());
                services.AddSingleton<IChatClientFactory>(new UsageBlockChatClientFactory());
                services.AddSingleton<IChatClientFactory>(new UsageThrowChatClientFactory());
                services.AddSingleton<IChatClientFactory>(new UsageCacheChatClientFactory());
                services.AddSingleton<IChatClientFactory>(new UsageResolvedCancelFactory());
                services.AddSingleton<IChatClientFactory>(new UsageResolvedErrorFactory());
                services.AddSingleton<IChatClientFactory>(new UsageNoProviderUsageCancelFactory());
                services.AddSingleton<IChatClientFactory>(new UsageNoProviderUsageErrorFactory());
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    // ─── Completed round ───

    [Fact]
    public async Task CompletedRound_StampsTokensOnResponseCell_AndAccumulatesOnThread()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "hello", modelName: "usage-model", createdBy: TestUser);

        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle
                 && t.Messages.Count >= 2
                 && t.IngestedMessageIds.Count >= 1,
            20_000);

        // Usage is recorded on the per-model TokenUsage satellite ({threadPath}/_Usage/{model}),
        // NOT on the thread node. Model id "usage-model" → key "usage_model".
        var usage = await WaitForUsage(threadPath, "usage_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, SettleBudgetMs);
        usage.Model.Should().Be("usage-model");
        usage.ThreadId.Should().Be(threadPath);

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, SettleBudgetMs);
        cell.InputTokens.Should().Be(InTokens);
        cell.OutputTokens.Should().Be(OutTokens);
        cell.TotalTokens.Should().Be(TotalTokens);
    }

    // ─── Observing usage from BEFORE the satellite exists (pins the read primitive) ───

    /// <summary>
    /// A consumer that starts watching a thread's usage BEFORE any round has run — i.e. while
    /// <c>{threadPath}/_Usage</c> holds nothing at all — must still receive the usage when it lands.
    ///
    /// <para>This is the ONLY ordering the platform actually guarantees. <c>TokenUsageNodeType.RecordUsage</c>
    /// is subscribed as an INDEPENDENT side effect, deliberately NOT chained before the round's
    /// terminal status write, so "the thread reached a terminal state" does NOT imply "the satellite
    /// exists". Every other test in this class reads the satellite AFTER waiting for the terminal
    /// state and therefore only samples the lucky ordering; this one pins the unlucky one.</para>
    ///
    /// <para>It is also the shape production uses: <c>ThreadTokenChip</c> opens its live
    /// <c>path:{thread}/_Usage scope:children</c> query the moment the chip is parameterised — long
    /// before the first round writes anything. A point <c>GetMeshNodeStream({thread}/_Usage/{model})</c>
    /// read cannot serve that: an absent node answers with an authoritative routing NotFound, which
    /// terminates the stream with an error rather than waiting for the node to appear. Under load
    /// (CI, or four test classes in flight) the round loses that race and the read errors within a
    /// second — three of this class's tests failed exactly that way at
    /// <c>DOTNET_PROCESSOR_COUNT=4 -parallel collections</c> (#1040).</para>
    /// </summary>
    [Fact]
    public async Task UsageWatchedFromBeforeTheRound_ArrivesWhenTheSatelliteIsCreated()
    {
        var threadPath = await SeedThread();
        var client = GetClient();

        // Open the observation FIRST — at this point the thread has no _Usage namespace at all, so
        // this is the "watcher was already there" ordering, deterministically.
        var watching = WaitForUsage(threadPath, "usage_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, SettleBudgetMs);

        client.SubmitMessage(threadPath, "hello", modelName: "usage-model", createdBy: TestUser);

        var usage = await watching;
        usage.Model.Should().Be("usage-model");
        usage.ThreadId.Should().Be(threadPath);
    }

    // ─── The satellite is never observable with zero tokens (#1812) ───

    /// <summary>
    /// A round that consumed tokens must NEVER publish a readable all-zero <see cref="TokenUsage"/>
    /// satellite — not even for a moment. Every emission that carries the node carries the counts.
    ///
    /// <para>This is the defect behind #1812, and it was a WRITE-side one.
    /// <c>TokenUsageNodeType.RecordUsage</c> used to write in two phases whose FIRST phase created
    /// the satellite with all four counters at zero and whose second added the round's tokens on
    /// top. That published a durable, readable, all-zero record in the window between them —
    /// measured at ~60 ms idle on this suite, unbounded under load. It was not a stale index view of
    /// a value already written; it was the node's real value, so an "authoritative" point read
    /// returned the same zeros.</para>
    ///
    /// <para>Two things went wrong with it, and only the second was ever noticed:
    /// the GUI's <c>ThreadTokenChip</c> renders that window as <c>↑0 ↓0 · $0</c>; and a reader
    /// waiting for the counts had to out-wait an intermediate that carried the right model and the
    /// wrong numbers. CI run 32070434174 lost that wait — it reported
    /// <c>Last of 1 emission(s) … InputTokens = 0</c> after 10 s, which reads as a lagging reader and
    /// is really a premature writer. And because the write chain's 15 s cap fails OPEN, a phase 2
    /// that never lands leaves those zeros as the PERMANENT record of a round that did cost money.
    /// </para>
    ///
    /// <para>The watcher is opened BEFORE the round so it is live across every write the satellite
    /// receives: the underlying query re-runs on each change with no debounce, so a durable zero
    /// state cannot slip past it. Pre-fix this failed on the first emission, every run.</para>
    ///
    /// <para>🚨 The zero-watch deliberately reads through
    /// <see cref="ObserveUsageAsThePortalChipDoes"/> — the query-shaped reader — because the claim
    /// is about what a query-bound GUI reader can OBSERVE, and pointing it at
    /// <see cref="ObserveUsage"/> would quietly gut it: that helper's point-read leg opens only once
    /// the index has already seen the node, so a regressed zero window could land and be overwritten
    /// before the leg ever subscribes, and this test would pass having watched nothing. The SETTLE,
    /// by contrast, runs on <see cref="ObserveUsage"/>, so the test's pass does not hang on the
    /// query index's lag — the assertion keeps its subject, without inheriting its flakiness.</para>
    /// </summary>
    [Fact]
    public async Task UsageSatelliteIsNeverObservableWithZeroTokens()
    {
        var threadPath = await SeedThread();
        var client = GetClient();

        var gate = new Lock();
        var zeroSnapshots = new List<TokenUsage>();

        // Live from before the round — see the summary: this must see every state the node passes
        // through, not just the one it settles on.
        using var zeroWatch = ObserveUsageAsThePortalChipDoes(threadPath, "usage_model")
            .Subscribe(u =>
            {
                if (u.InputTokens == 0 && u.OutputTokens == 0
                    && u.CacheReadTokens == 0 && u.CacheWriteTokens == 0)
                    lock (gate) zeroSnapshots.Add(u);
            });

        var settled = ObserveUsage(threadPath, "usage_model")
            .Should().Within(TimeSpan.FromSeconds(20))
            .Match(u => u.InputTokens == InTokens && u.OutputTokens == OutTokens);

        client.SubmitMessage(threadPath, "hello", modelName: "usage-model", createdBy: TestUser);

        await settled;

        List<TokenUsage> observedZeros;
        lock (gate) observedZeros = [.. zeroSnapshots];
        observedZeros.Should().BeEmpty(
            "a round that consumed {0} in / {1} out tokens must never publish a readable all-zero "
            + "usage satellite — the GUI chip renders that window as $0, and if the follow-up write "
            + "never lands (RecordUsage's cap fails open) the zeros become the permanent record",
            InTokens, OutTokens);
    }

    // ─── Cumulative across rounds (pins the round-dispatch reset hole) ───

    [Fact]
    public async Task MultipleCompletedRounds_AccumulateCumulatively_NotResetPerRound()
    {
        var threadPath = await SeedThread();
        var client = GetClient();

        client.SubmitMessage(threadPath, "round one", modelName: "usage-model", createdBy: TestUser);
        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, SettleBudgetMs);
        await WaitForUsage(threadPath, "usage_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, SettleBudgetMs);

        client.SubmitMessage(threadPath, "round two", modelName: "usage-model", createdBy: TestUser);
        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 4, SettleBudgetMs);

        // The per-model TokenUsage satellite ACCUMULATES across rounds — a second round adds onto
        // the first (each terminal RecordUsage reads the current satellite value and adds).
        await WaitForUsage(threadPath, "usage_model",
            u => u.InputTokens == InTokens * 2 && u.OutputTokens == OutTokens * 2, SettleBudgetMs);
    }

    // ─── Cancelled round (pins the dropped-usage hole on cancel) ───

    [Fact]
    public async Task CancelledRound_RecordsTokensConsumedBeforeCancel_OnCellAndThread()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "cancel me", modelName: "usage-cancel-model", createdBy: TestUser);

        // Round started and the active response cell exists.
        var executing = await WaitForThread(threadPath,
            t => t.IsExecuting && t.ActiveMessageId != null, SettleBudgetMs);
        var cellId = executing.ActiveMessageId!;

        // Wait until the post-usage marker lands on the cell — proves the usage update (yielded
        // BEFORE the marker) was already aggregated by the streaming loop. THEN request cancel.
        await WaitForCell(threadPath, cellId, m => (m.Text ?? "").Contains(UsageMarker), SettleBudgetMs);

        // Cancel via the canonical control-plane write (RequestedStatus on the node).
        await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .FirstAsync().ToTask();

        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Cancelled, SettleBudgetMs);

        await WaitForUsage(threadPath, "usage_cancel_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, SettleBudgetMs);

        var cell = await WaitForCell(threadPath, cellId,
            m => m.Status == ThreadMessageStatus.Cancelled, SettleBudgetMs);
        cell.InputTokens.Should().Be(InTokens,
            "the cancelled cell records the tokens consumed before the cancel");
        cell.OutputTokens.Should().Be(OutTokens);
        cell.TotalTokens.Should().Be(TotalTokens);
    }

    // ─── Error round (pins the dropped-usage hole on fault) ───

    [Fact]
    public async Task ErrorRound_RecordsTokensConsumedBeforeFault_OnCellAndThread()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "throw after usage", modelName: "usage-error-model", createdBy: TestUser);

        var terminal = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle
                 && t.IngestedMessageIds.Count >= 1
                 && t.Messages.Count >= 2,
            20_000);

        await WaitForUsage(threadPath, "usage_error_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, SettleBudgetMs);

        var cell = await WaitForCell(threadPath, terminal.Messages[^1],
            m => m.Status == ThreadMessageStatus.Error, SettleBudgetMs);
        cell.InputTokens.Should().Be(InTokens,
            "the errored cell records the tokens consumed before the fault");
        cell.OutputTokens.Should().Be(OutTokens);
        cell.TotalTokens.Should().Be(TotalTokens);
    }

    // ─── Cancelled/Error attribution (pins actualModel ?? request.ModelName, #595) ───
    //
    // The stream's updates carry a RESOLVED ModelId (like a harness resolving "sonnet" to a
    // concrete id, or a delegation sub-thread whose request.ModelName is null). The terminal
    // paths must key the TokenUsage satellite — and stamp the response cell — by that ACTUAL
    // model, not the bare requested alias. Before f97b44fa9 the Cancelled/Error satellite was
    // keyed by request.ModelName (null on a sub-thread → "(unknown)"); the cell's model was
    // still the bare alias until this fix.

    [Fact]
    public async Task CancelledRound_KeysUsageAndCellByActualModel_NotRequestedAlias()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "cancel me resolved", modelName: "usage-cancel-alias", createdBy: TestUser);

        var executing = await WaitForThread(threadPath,
            t => t.IsExecuting && t.ActiveMessageId != null, SettleBudgetMs);
        var cellId = executing.ActiveMessageId!;

        // Marker on the cell proves the usage update (and the ModelId-stamped chunks) were
        // aggregated before we request the cancel — same deterministic gate as the plain
        // Cancelled test, no sleep.
        await WaitForCell(threadPath, cellId, m => (m.Text ?? "").Contains(UsageMarker), SettleBudgetMs);

        await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .FirstAsync().ToTask();

        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Cancelled, SettleBudgetMs);

        // Satellite keyed by the RESOLVED model — if the code regressed to request.ModelName
        // this wait times out (the satellite would sit at usage_cancel_alias instead).
        var usage = await WaitForUsage(threadPath, "usage_cancel_resolved",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, SettleBudgetMs);
        usage.Model.Should().Be("usage-cancel-resolved",
            "the satellite records the model that actually ran, not the requested alias");

        var cell = await WaitForCell(threadPath, cellId,
            m => m.Status == ThreadMessageStatus.Cancelled, SettleBudgetMs);
        cell.ModelName.Should().Be("usage-cancel-resolved",
            "the cancelled cell shows the resolved model, matching the Completed path");
    }

    [Fact]
    public async Task ErrorRound_KeysUsageAndCellByActualModel_NotRequestedAlias()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "throw after usage resolved", modelName: "usage-error-alias", createdBy: TestUser);

        var terminal = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle
                 && t.IngestedMessageIds.Count >= 1
                 && t.Messages.Count >= 2,
            20_000);

        var usage = await WaitForUsage(threadPath, "usage_error_resolved",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, SettleBudgetMs);
        usage.Model.Should().Be("usage-error-resolved",
            "the satellite records the model that actually ran, not the requested alias");

        var cell = await WaitForCell(threadPath, terminal.Messages[^1],
            m => m.Status == ThreadMessageStatus.Error, SettleBudgetMs);
        cell.ModelName.Should().Be("usage-error-resolved",
            "the errored cell shows the resolved model, matching the Completed path");
        cell.InputTokens.Should().Be(InTokens);
        cell.OutputTokens.Should().Be(OutTokens);
    }

    // ─── Provider reports NOTHING before the terminal path (#595 — the estimate floor) ───
    //
    // An OpenAI-compatible provider emits usage ONLY in a successful stream's terminal chunk —
    // a cancel or a fault pre-empting that chunk means the streaming loop above never saw a
    // UsageContent block at all, so inputTokens/outputTokens are still null when the terminal
    // path runs. Before this fix RecordUsage's all-zero guard made this a silent no-op: the round
    // vanished from accounting even though the provider had already billed the prompt it
    // processed. These pin the ESTIMATE floor: TokenUsageNodeType.EstimateTokens derives a
    // non-zero input estimate from the prompt actually sent (allMessages) and a non-zero output
    // estimate from the text actually streamed before the terminal path, and the satellite is
    // flagged IsEstimated so a reader never conflates it with a provider-reported count.

    [Fact]
    public async Task CancelledRound_ProviderReportsNothing_RecordsEstimatedTokens()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "cancel me, provider never reports usage", modelName: "usage-cancel-noreport-model", createdBy: TestUser);

        var executing = await WaitForThread(threadPath,
            t => t.IsExecuting && t.ActiveMessageId != null, SettleBudgetMs);
        var cellId = executing.ActiveMessageId!;

        // No usage marker exists on this path (the provider never emits UsageContent) — gate on
        // the text that DOES stream before requesting the cancel, so it deterministically lands
        // after the streaming loop has something to estimate from.
        await WaitForCell(threadPath, cellId, m => (m.Text ?? "").Contains(NoReportWorkingText), SettleBudgetMs);

        // ContentAs, not `curr?.Content is MeshThread t` — Copilot review, PR #2375: the trap-door
        // AGENTS.md forbids (a degraded JsonElement/DOM shape would silently skip the update).
        await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(curr =>
            {
                var t = curr.ContentAs<MeshThread>(Mesh.JsonSerializerOptions);
                return t is not null
                    ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                    : curr!;
            })
            .FirstAsync().ToTask();

        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Cancelled, SettleBudgetMs);

        var usage = await WaitForUsage(threadPath, "usage_cancel_noreport_model",
            u => u.InputTokens > 0, SettleBudgetMs);
        usage.IsEstimated.Should().BeTrue(
            "the provider reported no usage at all before the cancel — the recorded counts are a character estimate, never a provider count");
        usage.InputTokens.Should().BeGreaterThan(0,
            "the prompt actually sent is known before the request was issued, even when the provider never confirms it");
        usage.OutputTokens.Should().BeGreaterThan(0,
            "the text chunk streamed before the cancel — that much output is known too");

        var cell = await WaitForCell(threadPath, cellId,
            m => m.Status == ThreadMessageStatus.Cancelled, SettleBudgetMs);
        cell.InputTokens.GetValueOrDefault().Should().BeGreaterThan(0,
            "the cancelled cell reflects the same estimate recorded on the satellite");
    }

    [Fact]
    public async Task ErrorRound_ProviderReportsNothing_RecordsEstimatedTokens()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "throw, provider never reports usage", modelName: "usage-error-noreport-model", createdBy: TestUser);

        var terminal = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle
                 && t.IngestedMessageIds.Count >= 1
                 && t.Messages.Count >= 2,
            20_000);

        var usage = await WaitForUsage(threadPath, "usage_error_noreport_model",
            u => u.InputTokens > 0, SettleBudgetMs);
        usage.IsEstimated.Should().BeTrue(
            "the provider reported no usage at all before the fault — the recorded counts are a character estimate, never a provider count");
        usage.InputTokens.Should().BeGreaterThan(0);
        usage.OutputTokens.Should().BeGreaterThan(0,
            "the text chunk streamed before the throw — that much output is known too");

        var cell = await WaitForCell(threadPath, terminal.Messages[^1],
            m => m.Status == ThreadMessageStatus.Error, SettleBudgetMs);
        cell.InputTokens.GetValueOrDefault().Should().BeGreaterThan(0,
            "the errored cell reflects the same estimate recorded on the satellite");
    }

    // ─── Provider reports only in/out (pins the total-derivation fallback) ───

    [Fact]
    public async Task CompletedRound_ProviderOmitsTotal_DerivesTotalFromInPlusOut()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "no total reported", modelName: "usage-nototal-model", createdBy: TestUser);

        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, SettleBudgetMs);

        // The satellite stores in/out; the total is derived on the cell when the provider omits it.
        await WaitForUsage(threadPath, "usage_nototal_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, SettleBudgetMs);

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, SettleBudgetMs);
        cell.InputTokens.Should().Be(InTokens);
        cell.OutputTokens.Should().Be(OutTokens);
        cell.TotalTokens.Should().Be(TotalTokens, "derived total = in + out");
    }

    // ─── Prompt cache (pins the dropped cache-token hole across providers) ───

    [Fact]
    public async Task CompletedRound_WithPromptCache_RecordsCacheTokens_OnCellAndSatellite()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "cache me", modelName: "usage-cache-model", createdBy: TestUser);

        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, SettleBudgetMs);

        // The cache read/write counts must survive from UsageDetails.AdditionalCounts (mixed provider
        // keys) all the way onto the per-model satellite — they used to be dropped entirely.
        var usage = await WaitForUsage(threadPath, "usage_cache_model",
            u => u.CacheReadTokens == CacheReadTokens && u.CacheWriteTokens == CacheWriteTokens, SettleBudgetMs);
        usage.InputTokens.Should().Be(InTokens, "input is the full prompt total; cache is a subset");
        usage.OutputTokens.Should().Be(OutTokens);

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, SettleBudgetMs);
        cell.CacheReadTokens.Should().Be(CacheReadTokens);
        cell.CacheWriteTokens.Should().Be(CacheWriteTokens);
        cell.InputTokens.Should().Be(InTokens);
    }

    // ─── Helpers ───

    private async Task<string> SeedThread()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Token Usage Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = TestUser }
        }).Should().Emit();
        return threadPath;
    }

    private async Task<MeshThread> WaitForThread(string threadPath, Func<MeshThread, bool> predicate, int timeoutMs)
        => (await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(t => predicate(t!)))!;

    private async Task<ThreadMessage> WaitForCell(string threadPath, string cellId, Func<ThreadMessage, bool> predicate, int timeoutMs)
        => (await Mesh.GetWorkspace().GetMeshNodeStream($"{threadPath}/{cellId}")
            .Select(n => n?.Content as ThreadMessage)
            .Where(m => m is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(m => predicate(m!)))!;

    /// <summary>
    /// The live per-model <see cref="TokenUsage"/> satellite at <c>{threadPath}/_Usage/{modelKey}</c>,
    /// as every emission the reader can observe (nulls — "not there yet" — filtered out).
    ///
    /// <para><b>Two legs, and the split is the whole point: EXISTENCE from a listing, CONTENT from
    /// the owner.</b> Reading an optional node has two rules pulling opposite ways, and every
    /// previous version of this helper picked one horn and was bitten by the other:</para>
    /// <list type="bullet">
    ///   <item><b>A point read cannot wait for a node to appear.</b>
    ///     <c>GetMeshNodeStream({usagePath})</c> on an ABSENT node answers with an authoritative
    ///     routing NotFound and TERMINATES the stream with an error — and that NotFound opens
    ///     <c>MeshNodeStreamCache</c>'s storm-breaker window on the very path
    ///     <c>RecordUsage</c> is about to write, which fast-fails the WRITE too. That is #1040's
    ///     <c>No node found at …/_Usage/…</c>, an error rather than a timeout, and it is why
    ///     <c>02b851fd6</c> moved this helper onto a query at all. <c>RecordUsage</c> is subscribed
    ///     as an INDEPENDENT side effect, deliberately NOT chained before the round's terminal
    ///     status write, so "the round reached a terminal state" never implies "the satellite
    ///     exists" — a reader genuinely has to cope with the node not being there yet.</item>
    ///   <item><b>A query cannot answer for a known path's CONTENT.</b> The query index is
    ///     eventually consistent; AGENTS.md → "Never Query for a Single Node's Content" forbids
    ///     exactly this, and the 2026-08-25 tightening of
    ///     <c>Doc/Architecture/CqrsAndContentAccess</c> (#2229) put a number on it: a query's answer
    ///     for one path can be minutes old. Waiting on a VALUE through that index is waiting on an
    ///     unbounded lag, which is what turned this class into a repeat CI offender — #1812, #2001
    ///     and run 32876073965 are all the same shape, "the observable emitted nothing at all" on a
    ///     <c>WaitForUsage</c>, never on <c>WaitForThread</c>/<c>WaitForCell</c> despite identical
    ///     budgets in the same test methods.</item>
    /// </list>
    ///
    /// <para>So: the children LISTING answers only "is it there yet" — the one query use AGENTS.md
    /// still sanctions, empty-on-absent, where a stale negative merely makes us wait a beat longer —
    /// and the moment it says yes, CONTENT comes off the OWNER's authoritative
    /// <c>GetMeshNodeStream(usagePath)</c>, which is never stale and stays live so accumulation
    /// across rounds is observed. The point read only ever opens on a node the index has already
    /// seen, so it cannot NotFound and cannot poison the writer. Both horns satisfied; neither rule
    /// bent. <see cref="UsageWatchedFromBeforeTheRound_ArrivesWhenTheSatelliteIsCreated"/> pins the
    /// watcher-first ordering deterministically.</para>
    ///
    /// <para>🚨 Do not "simplify" this back to either half. Switching wholesale to the node stream
    /// re-opens #1040 (NotFound + storm breaker); reading <c>content</c> out of the query re-opens
    /// the unbounded-lag wait. Note also that widening the wait is not a repair: #2001's fix
    /// (<c>3fcf79f8f</c>) replaced every 10 s budget with 20 s, and the same assertion then failed
    /// at 20 s.</para>
    /// </summary>
    private IObservable<TokenUsage> ObserveUsage(string threadPath, string modelKey)
    {
        var usagePath = $"{threadPath}/{TokenUsageNodeType.SatelliteSegment}/{modelKey}";
        // Leg 1 — EXISTENCE, via the children listing. select:path only: nothing here reads Content.
        return ObserveUsageNamespace(threadPath, "select:path")
            .Where(nodes => nodes.Any(n =>
                string.Equals(n.Path, usagePath, StringComparison.OrdinalIgnoreCase)))
            .Take(1)
            // Leg 2 — CONTENT, from the owner. Live (no Take): rounds 2+ accumulate onto the same node.
            .SelectMany(_ => Mesh.GetWorkspace().GetMeshNodeStream(usagePath))
            .Select(n => n.ContentAs<TokenUsage>(Mesh.JsonSerializerOptions))
            .Where(u => u is not null)
            .Select(u => u!);
    }

    /// <summary>
    /// The satellite as the PORTAL's <c>ThreadTokenChip</c> sees it — the raw children query,
    /// content and all. This is deliberately the shape <see cref="ObserveUsage"/> refuses to use for
    /// a value wait, and it exists for exactly one job: letting
    /// <see cref="UsageSatelliteIsNeverObservableWithZeroTokens"/> assert what a query-bound GUI
    /// reader can observe, without also making the test's PASS depend on that reader's lag.
    /// </summary>
    private IObservable<TokenUsage> ObserveUsageAsThePortalChipDoes(string threadPath, string modelKey)
    {
        var usagePath = $"{threadPath}/{TokenUsageNodeType.SatelliteSegment}/{modelKey}";
        return ObserveUsageNamespace(threadPath, "select:path,id,namespace,name,nodeType,content")
            .Select(nodes => nodes
                .FirstOrDefault(n => string.Equals(n.Path, usagePath, StringComparison.OrdinalIgnoreCase))
                .ContentAs<TokenUsage>(Mesh.JsonSerializerOptions))
            .Where(u => u is not null)
            .Select(u => u!);
    }

    /// <summary>
    /// The live children listing of <c>{threadPath}/_Usage</c>. Distinct cache ids per projection —
    /// the query set is part of the cache key (#1311), and giving the two projections separate ids
    /// keeps that explicit rather than relying on it.
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> ObserveUsageNamespace(string threadPath, string select)
        => Mesh.GetQuery(
            $"usage:{threadPath}:{select}",
            $"path:{threadPath}/{TokenUsageNodeType.SatelliteSegment} scope:children "
            + $"nodeType:{TokenUsageNodeType.NodeType} {select}");

    /// <summary>
    /// Watches <see cref="ObserveUsage"/> until it matches <paramref name="predicate"/>.
    /// </summary>
    private Task<TokenUsage> WaitForUsage(string threadPath, string modelKey, Func<TokenUsage, bool> predicate, int timeoutMs)
        => ObserveUsage(threadPath, modelKey)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(predicate);

    // ─── Fake usage-reporting chat client ───

    private enum PostUsage { Complete, BlockUntilCancel, Throw }

    /// <summary>
    /// Streams a text chunk, then a <see cref="UsageContent"/> carrying the scripted token counts,
    /// then a post-usage text marker (so a consumer can prove the usage was aggregated). What it
    /// does after the marker is controlled by <see cref="PostUsage"/>: complete cleanly, block on
    /// the round CTS until cancelled (→ OperationCanceledException → Cancelled path), or throw
    /// (→ Error path). <paramref name="reportTotal"/> toggles whether TotalTokenCount is reported.
    /// <paramref name="modelId"/>, when set, stamps every update's ModelId — modelling a provider
    /// or harness that reports the RESOLVED model it actually ran (ThreadExecution captures it as
    /// actualModel), distinct from the requested alias the factory routes on.
    /// <paramref name="omitUsageContent"/>, when true, NEVER emits a <see cref="UsageContent"/>
    /// block at all — models an OpenAI-compatible provider that reports usage ONLY in the
    /// terminal chunk of a successful stream, which a cancel/fault pre-empts by construction
    /// (there is no terminal chunk to omit it from — the round never reaches one). Only "Working. "
    /// streams before <paramref name="mode"/> takes over, so the streaming loop's
    /// inputTokens/outputTokens stay null all the way to the terminal path (#595's estimate floor).
    /// </summary>
    private sealed class UsageChatClient(bool reportTotal, PostUsage mode, bool emitCache = false, string? modelId = null, bool omitUsageContent = false) : IChatClient
    {
        public ChatClientMetadata Metadata => new("UsageProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "usage ack")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Text first — the round is genuinely streaming past the Executing flip.
            // 🚨 omitUsageContent's text must OUTGROW the framework's own initial
            // "Generating response..." placeholder (22 chars): PushToResponseMessage's
            // monotonic-growth guard keeps the LONGER of current vs incoming text while
            // Status is Streaming, so a shorter chunk ("Working. ", 9 chars) would never
            // actually land on the cell — silently starving any test that waits for it
            // (caught by CancelledRound_ProviderReportsNothing_RecordsEstimatedTokens
            // timing out with the placeholder still showing, not a product bug).
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                omitUsageContent ? NoReportWorkingText : "Working. ") { ModelId = modelId };

            if (!omitUsageContent)
            {
                // The usage report — this is what ThreadExecution aggregates. When emitCache is set,
                // the cache breakdown rides in AdditionalCounts under MIXED provider keys (OpenAI's
                // "InputTokenDetails.CachedTokenCount" for read, Claude's "CacheCreationInputTokens"
                // for write) so the test proves UsageTokens.SplitCache is provider-agnostic.
                var details = new UsageDetails
                {
                    InputTokenCount = InTokens,
                    OutputTokenCount = OutTokens,
                    TotalTokenCount = reportTotal ? TotalTokens : (long?)null
                };
                if (emitCache)
                    details.AdditionalCounts = new AdditionalPropertiesDictionary<long>
                    {
                        ["InputTokenDetails.CachedTokenCount"] = CacheReadTokens,
                        [UsageTokens.CacheWriteKey] = CacheWriteTokens
                    };
                yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[]
                {
                    new UsageContent(details)
                }) { ModelId = modelId };
                // Post-usage marker — once it lands on the cell, the usage above was provably pulled.
                yield return new ChatResponseUpdate(ChatRole.Assistant, UsageMarker) { ModelId = modelId };
            }

            switch (mode)
            {
                case PostUsage.Complete:
                    await Task.Yield();
                    break;
                case PostUsage.BlockUntilCancel:
                    // Block until the round's CTS fires; Task.Delay throws
                    // OperationCanceledException on cancel → the Cancelled terminal path.
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
                case PostUsage.Throw:
                    await Task.Yield();
                    throw new InvalidOperationException("boom after usage");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private abstract class UsageFactoryBase : IChatClientFactory
    {
        public abstract string Name { get; }
        public abstract IReadOnlyList<string> Models { get; }
        public int Order => 0;
        protected abstract IChatClient CreateClient();

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(
                chatClient: CreateClient(),
                instructions: config.Instructions ?? "usage test assistant",
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

    private sealed class UsageChatClientFactory : UsageFactoryBase
    {
        public override string Name => "UsageFactory";
        public override IReadOnlyList<string> Models => ["usage-model"];
        protected override IChatClient CreateClient() => new UsageChatClient(reportTotal: true, PostUsage.Complete);
    }

    private sealed class UsageNoTotalChatClientFactory : UsageFactoryBase
    {
        public override string Name => "UsageNoTotalFactory";
        public override IReadOnlyList<string> Models => ["usage-nototal-model"];
        protected override IChatClient CreateClient() => new UsageChatClient(reportTotal: false, PostUsage.Complete);
    }

    private sealed class UsageBlockChatClientFactory : UsageFactoryBase
    {
        public override string Name => "UsageBlockFactory";
        public override IReadOnlyList<string> Models => ["usage-cancel-model"];
        protected override IChatClient CreateClient() => new UsageChatClient(reportTotal: true, PostUsage.BlockUntilCancel);
    }

    private sealed class UsageThrowChatClientFactory : UsageFactoryBase
    {
        public override string Name => "UsageThrowFactory";
        public override IReadOnlyList<string> Models => ["usage-error-model"];
        protected override IChatClient CreateClient() => new UsageChatClient(reportTotal: true, PostUsage.Throw);
    }

    private sealed class UsageCacheChatClientFactory : UsageFactoryBase
    {
        public override string Name => "UsageCacheFactory";
        public override IReadOnlyList<string> Models => ["usage-cache-model"];
        protected override IChatClient CreateClient() => new UsageChatClient(reportTotal: true, PostUsage.Complete, emitCache: true);
    }

    // Routes on the ALIAS; the stream reports the RESOLVED ModelId — the terminal Cancelled
    // path must attribute usage to the resolved model (#595).
    private sealed class UsageResolvedCancelFactory : UsageFactoryBase
    {
        public override string Name => "UsageResolvedCancelFactory";
        public override IReadOnlyList<string> Models => ["usage-cancel-alias"];
        protected override IChatClient CreateClient() => new UsageChatClient(
            reportTotal: true, PostUsage.BlockUntilCancel, modelId: "usage-cancel-resolved");
    }

    // Same for the Error terminal path.
    private sealed class UsageResolvedErrorFactory : UsageFactoryBase
    {
        public override string Name => "UsageResolvedErrorFactory";
        public override IReadOnlyList<string> Models => ["usage-error-alias"];
        protected override IChatClient CreateClient() => new UsageChatClient(
            reportTotal: true, PostUsage.Throw, modelId: "usage-error-resolved");
    }

    // Models an OpenAI-compatible provider: usage arrives ONLY in a successful terminal chunk,
    // which the cancel pre-empts — the terminal path must fall back to the character estimate.
    private sealed class UsageNoProviderUsageCancelFactory : UsageFactoryBase
    {
        public override string Name => "UsageNoProviderUsageCancelFactory";
        public override IReadOnlyList<string> Models => ["usage-cancel-noreport-model"];
        protected override IChatClient CreateClient() => new UsageChatClient(
            reportTotal: true, PostUsage.BlockUntilCancel, omitUsageContent: true);
    }

    // Same for the Error terminal path.
    private sealed class UsageNoProviderUsageErrorFactory : UsageFactoryBase
    {
        public override string Name => "UsageNoProviderUsageErrorFactory";
        public override IReadOnlyList<string> Models => ["usage-error-noreport-model"];
        protected override IChatClient CreateClient() => new UsageChatClient(
            reportTotal: true, PostUsage.Throw, omitUsageContent: true);
    }
}
