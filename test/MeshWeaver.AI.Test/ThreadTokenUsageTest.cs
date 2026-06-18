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
/// and (b) accumulate the thread's cumulative <see cref="MeshThread.TokensUsed"/> — on Completed,
/// Cancelled, AND Error rounds alike.
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

        thread.TokensUsed.Should().Be(TotalTokens,
            "the completed round's tokens accumulate onto the thread total");

        // Per-model breakdown is recorded too (model id = the bare submitted model name).
        thread.TokensByModel.Should().ContainKey("usage-model");
        thread.TokensByModel["usage-model"].InputTokens.Should().Be(InTokens);
        thread.TokensByModel["usage-model"].OutputTokens.Should().Be(OutTokens);

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
        var afterRound1 = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, 20_000);
        afterRound1.TokensUsed.Should().Be(TotalTokens, "first round's tokens land on the thread");

        client.SubmitMessage(threadPath, "round two", modelName: "usage-model", createdBy: TestUser);
        var afterRound2 = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 4, 20_000);

        afterRound2.TokensUsed.Should().Be(TotalTokens * 2,
            "TokensUsed is the thread's CUMULATIVE total — a second round adds onto the first; "
            + "the round-dispatch reset that wiped it to 0 each round is the bug being pinned");

        // The per-model breakdown accumulates cumulatively too.
        afterRound2.TokensByModel["usage-model"].InputTokens.Should().Be(InTokens * 2);
        afterRound2.TokensByModel["usage-model"].OutputTokens.Should().Be(OutTokens * 2);
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

        var cancelled = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Cancelled, 20_000);

        cancelled.TokensUsed.Should().Be(TotalTokens,
            "tokens consumed before the cancel must accumulate onto the thread total");

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

        terminal.TokensUsed.Should().Be(TotalTokens,
            "tokens consumed before the fault must accumulate onto the thread total");

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
        thread.TokensUsed.Should().Be(TotalTokens,
            "the total is derived from in+out when the provider omits TotalTokenCount");

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, 10_000);
        cell.InputTokens.Should().Be(InTokens);
        cell.OutputTokens.Should().Be(OutTokens);
        cell.TotalTokens.Should().Be(TotalTokens, "derived total = in + out");
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

    // ─── Fake usage-reporting chat client ───

    private enum PostUsage { Complete, BlockUntilCancel, Throw }

    /// <summary>
    /// Streams a text chunk, then a <see cref="UsageContent"/> carrying the scripted token counts,
    /// then a post-usage text marker (so a consumer can prove the usage was aggregated). What it
    /// does after the marker is controlled by <see cref="PostUsage"/>: complete cleanly, block on
    /// the round CTS until cancelled (→ OperationCanceledException → Cancelled path), or throw
    /// (→ Error path). <paramref name="reportTotal"/> toggles whether TotalTokenCount is reported.
    /// </summary>
    private sealed class UsageChatClient(bool reportTotal, PostUsage mode) : IChatClient
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
            // The usage report — this is what ThreadExecution aggregates.
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[]
            {
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = InTokens,
                    OutputTokenCount = OutTokens,
                    TotalTokenCount = reportTotal ? TotalTokens : (long?)null
                })
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
}
