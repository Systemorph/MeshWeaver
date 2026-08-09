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
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 10_000);
        usage.Model.Should().Be("usage-model");
        usage.ThreadId.Should().Be(threadPath);

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, 10_000);
        cell.InputTokens.Should().Be(InTokens);
        cell.OutputTokens.Should().Be(OutTokens);
        cell.TotalTokens.Should().Be(TotalTokens);
    }

    // ─── Observing usage from BEFORE the satellite exists (pins the read primitive) ───

    /// <summary>
    /// A consumer that starts watching a thread's usage BEFORE any round has run — i.e. while
    /// <c>{threadPath}/_Usage</c> holds nothing at all — must still receive the usage when it lands.
    ///
    /// <para>This is the ONLY ordering the platform actually guarantees. <c>TokenUsage.RecordUsage</c>
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
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 20_000);

        client.SubmitMessage(threadPath, "hello", modelName: "usage-model", createdBy: TestUser);

        var usage = await watching;
        usage.Model.Should().Be("usage-model");
        usage.ThreadId.Should().Be(threadPath);
    }

    // ─── Cumulative across rounds (pins the round-dispatch reset hole) ───

    [Fact]
    public async Task MultipleCompletedRounds_AccumulateCumulatively_NotResetPerRound()
    {
        var threadPath = await SeedThread();
        var client = GetClient();

        client.SubmitMessage(threadPath, "round one", modelName: "usage-model", createdBy: TestUser);
        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, 20_000);
        await WaitForUsage(threadPath, "usage_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 10_000);

        client.SubmitMessage(threadPath, "round two", modelName: "usage-model", createdBy: TestUser);
        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 4, 20_000);

        // The per-model TokenUsage satellite ACCUMULATES across rounds — a second round adds onto
        // the first (each terminal RecordUsage reads the current satellite value and adds).
        await WaitForUsage(threadPath, "usage_model",
            u => u.InputTokens == InTokens * 2 && u.OutputTokens == OutTokens * 2, 10_000);
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
            t => t.IsExecuting && t.ActiveMessageId != null, 20_000);
        var cellId = executing.ActiveMessageId!;

        // Wait until the post-usage marker lands on the cell — proves the usage update (yielded
        // BEFORE the marker) was already aggregated by the streaming loop. THEN request cancel.
        await WaitForCell(threadPath, cellId, m => (m.Text ?? "").Contains(UsageMarker), 20_000);

        // Cancel via the canonical control-plane write (RequestedStatus on the node).
        await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .FirstAsync().ToTask();

        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Cancelled, 20_000);

        await WaitForUsage(threadPath, "usage_cancel_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 10_000);

        var cell = await WaitForCell(threadPath, cellId,
            m => m.Status == ThreadMessageStatus.Cancelled, 10_000);
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
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 10_000);

        var cell = await WaitForCell(threadPath, terminal.Messages[^1],
            m => m.Status == ThreadMessageStatus.Error, 10_000);
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
            t => t.IsExecuting && t.ActiveMessageId != null, 20_000);
        var cellId = executing.ActiveMessageId!;

        // Marker on the cell proves the usage update (and the ModelId-stamped chunks) were
        // aggregated before we request the cancel — same deterministic gate as the plain
        // Cancelled test, no sleep.
        await WaitForCell(threadPath, cellId, m => (m.Text ?? "").Contains(UsageMarker), 20_000);

        await client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .FirstAsync().ToTask();

        await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Cancelled, 20_000);

        // Satellite keyed by the RESOLVED model — if the code regressed to request.ModelName
        // this wait times out (the satellite would sit at usage_cancel_alias instead).
        var usage = await WaitForUsage(threadPath, "usage_cancel_resolved",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 10_000);
        usage.Model.Should().Be("usage-cancel-resolved",
            "the satellite records the model that actually ran, not the requested alias");

        var cell = await WaitForCell(threadPath, cellId,
            m => m.Status == ThreadMessageStatus.Cancelled, 10_000);
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
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 10_000);
        usage.Model.Should().Be("usage-error-resolved",
            "the satellite records the model that actually ran, not the requested alias");

        var cell = await WaitForCell(threadPath, terminal.Messages[^1],
            m => m.Status == ThreadMessageStatus.Error, 10_000);
        cell.ModelName.Should().Be("usage-error-resolved",
            "the errored cell shows the resolved model, matching the Completed path");
        cell.InputTokens.Should().Be(InTokens);
        cell.OutputTokens.Should().Be(OutTokens);
    }

    // ─── Provider reports only in/out (pins the total-derivation fallback) ───

    [Fact]
    public async Task CompletedRound_ProviderOmitsTotal_DerivesTotalFromInPlusOut()
    {
        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "no total reported", modelName: "usage-nototal-model", createdBy: TestUser);

        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, 20_000);

        // The satellite stores in/out; the total is derived on the cell when the provider omits it.
        await WaitForUsage(threadPath, "usage_nototal_model",
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 10_000);

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, 10_000);
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
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, 20_000);

        // The cache read/write counts must survive from UsageDetails.AdditionalCounts (mixed provider
        // keys) all the way onto the per-model satellite — they used to be dropped entirely.
        var usage = await WaitForUsage(threadPath, "usage_cache_model",
            u => u.CacheReadTokens == CacheReadTokens && u.CacheWriteTokens == CacheWriteTokens, 10_000);
        usage.InputTokens.Should().Be(InTokens, "input is the full prompt total; cache is a subset");
        usage.OutputTokens.Should().Be(OutTokens);

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, 10_000);
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
    /// Watches the per-model <see cref="TokenUsage"/> satellite at
    /// <c>{threadPath}/_Usage/{modelKey}</c> until it matches <paramref name="predicate"/>.
    ///
    /// <para>🚨 Through the LIVE CHILDREN QUERY of <c>{threadPath}/_Usage</c> — byte for byte the
    /// primitive <c>ThreadTokenChip</c> binds to in the portal — and never a point
    /// <c>GetMeshNodeStream({threadPath}/_Usage/{modelKey})</c> read. The satellite is written by
    /// <c>TokenUsage.RecordUsage</c>, which is subscribed as an INDEPENDENT side effect and
    /// deliberately NOT chained before the round's terminal status write; a watcher can therefore be
    /// in place before the node exists. A point read of an absent node answers with an authoritative
    /// routing NotFound and TERMINATES the stream with an error — it cannot wait for a node to
    /// appear. The children query starts from the (possibly empty) collection and re-emits when the
    /// node lands, which is the only shape that serves this ordering.</para>
    ///
    /// <para>That mismatch was #1040's largest blocker: at
    /// <c>DOTNET_PROCESSOR_COUNT=4 -parallel collections</c> three of this class's tests failed
    /// inside a second with <c>No node found at …/_Usage/…</c> — not a timeout, an error. Serial
    /// scheduling merely let the create win the race.
    /// <see cref="UsageWatchedFromBeforeTheRound_ArrivesWhenTheSatelliteIsCreated"/> pins the losing
    /// ordering deterministically.</para>
    /// </summary>
    private async Task<TokenUsage> WaitForUsage(string threadPath, string modelKey, Func<TokenUsage, bool> predicate, int timeoutMs)
    {
        var usagePath = $"{threadPath}/{TokenUsageNodeType.SatelliteSegment}/{modelKey}";
        return (await Mesh.GetQuery(
                $"usage:{threadPath}",
                $"path:{threadPath}/{TokenUsageNodeType.SatelliteSegment} scope:children "
                + $"nodeType:{TokenUsageNodeType.NodeType} select:path,id,namespace,name,nodeType,content")
            .Select(nodes => nodes
                .FirstOrDefault(n => string.Equals(n.Path, usagePath, StringComparison.OrdinalIgnoreCase))
                .ContentAs<TokenUsage>(Mesh.JsonSerializerOptions))
            .Where(u => u is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(u => predicate(u!)))!;
    }

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
    /// </summary>
    private sealed class UsageChatClient(bool reportTotal, PostUsage mode, bool emitCache = false, string? modelId = null) : IChatClient
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
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Working. ") { ModelId = modelId };
            // The usage report — this is what ThreadExecution aggregates. When emitCache is set, the
            // cache breakdown rides in AdditionalCounts under MIXED provider keys (OpenAI's
            // "InputTokenDetails.CachedTokenCount" for read, Claude's "CacheCreationInputTokens" for
            // write) so the test proves UsageTokens.SplitCache is provider-agnostic.
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
}
