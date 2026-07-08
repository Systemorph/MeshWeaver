#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
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

    // The per-model TokenUsage satellite at {threadPath}/_Usage/{modelKey}.
    private async Task<TokenUsage> WaitForUsage(string threadPath, string modelKey, Func<TokenUsage, bool> predicate, int timeoutMs)
        => (await Mesh.GetWorkspace().GetMeshNodeStream($"{threadPath}/{TokenUsageNodeType.SatelliteSegment}/{modelKey}")
            .Select(n => n?.Content as TokenUsage)
            .Where(u => u is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(u => predicate(u!)))!;

    // ─── Fake usage-reporting chat client ───

    private enum PostUsage { Complete, BlockUntilCancel, Throw }

    /// <summary>
    /// Streams a text chunk, then a <see cref="UsageContent"/> carrying the scripted token counts,
    /// then a post-usage text marker (so a consumer can prove the usage was aggregated). What it
    /// does after the marker is controlled by <see cref="PostUsage"/>: complete cleanly, block on
    /// the round CTS until cancelled (→ OperationCanceledException → Cancelled path), or throw
    /// (→ Error path). <paramref name="reportTotal"/> toggles whether TotalTokenCount is reported.
    /// </summary>
    private sealed class UsageChatClient(bool reportTotal, PostUsage mode, bool emitCache = false) : IChatClient
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
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Working. ");
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
            });
            // Post-usage marker — once it lands on the cell, the usage above was provably pulled.
            yield return new ChatResponseUpdate(ChatRole.Assistant, UsageMarker);

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
}
