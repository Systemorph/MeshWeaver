#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Pins the fix for GitHub issue #2226 (duplicated as #2227 / #2228): what a round must do when the
/// thread's prior conversation CANNOT be loaded.
///
/// <para><c>LoadConversationHistoryTest</c> covers the loader's own contract — it already refuses to
/// return an empty list when cells were expected, throwing instead. The defect lived one level up:
/// <c>ThreadExecution</c> caught that refusal and continued with <c>Array.Empty&lt;ChatMessage&gt;()</c>,
/// logging <c>HISTORY_LOAD_FAILED … proceeding with EMPTY history</c>. The agent then answered a
/// long-running thread as though it had just been created, and the round settled <b>Completed</b> —
/// so nothing downstream could tell a context-less answer from a correct one. Seven such rounds
/// shipped wrong answers over three days in production.</para>
///
/// <para>The distinction this test enforces is the whole point: <b>"no history" and "could not load
/// the history" are different facts.</b> A brand-new thread legitimately has none and must run; a
/// thread whose cells could not be read must FAIL, loudly and legibly, rather than silently answer
/// with less than it should have had.</para>
/// </summary>
public class HistoryLoadFailureRoundTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";
    private const string FakeResponse = "Test response.";

    /// <summary>
    /// Counts agent invocations. THE discriminating assertion: before the fix the model was called
    /// (with an empty history) and produced a plausible-looking answer; after it, the round fails
    /// before any provider is reached. Instance state on the test class — one mesh per test, so
    /// nothing is shared and nothing needs clearing.
    /// </summary>
    private readonly InvocationCounter invocations = new();

    // ─── Timeout budget ───
    // The dominant cost is REAL and intentional: the loader spends its production per-cell budget
    // (5s, unpassed here on purpose — this must exercise the shipped configuration) waiting on cells
    // that will never arrive. Everything else is in-memory mesh plumbing.
    private const int LoaderCellBudgetMs = 5_000;
    private const int SeedMs = 20_000;
    private const int RoundMs = 30_000 + LoaderCellBudgetMs;
    private const int CellMs = 10_000;

    /// <summary>
    /// 🚨 <c>ThreadFlow.ReadThread</c> / <c>ReadMessage</c> carry their OWN <c>.Timeout(...)</c>
    /// (30s / 15s by default). Every wait below therefore passes its stage bound EXPLICITLY and sets
    /// the outer assertion bound one margin higher, so the timeout that fires is always the inner,
    /// stage-named one — never a bare outer abort that says only "something took too long".
    /// </summary>
    private const int WaitMarginMs = 3_000;

    private const int InnerBudgetMs =
        (SeedMs + WaitMarginMs) * 4 + (RoundMs + WaitMarginMs) + (CellMs + WaitMarginMs);
    private const int MethodTimeoutMs = InnerBudgetMs * 4 / 3;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory(invocations));
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData();
    }

    /// <summary>
    /// ⏱️ Ceiling = <c>InnerBudgetMs × 4/3</c>, derived from the stage bounds (4 setup waits + the
    /// round + the terminal cell read, each plus its margin) rather than written as a literal, so it
    /// can never drift below the sum it must outlive. The round bound deliberately INCLUDES the
    /// loader's own 5s per-cell budget: this test exercises the shipped configuration, so it pays
    /// the real wait rather than shrinking it to make the suite faster.
    /// </summary>
    [Fact(Timeout = MethodTimeoutMs)]
    public async Task HistoryLoadFails_RoundErrorsLegibly_AndNeverAnswersWithEmptyHistory()
    {
        var client = GetClient();

        var createResp = await client.Observe(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(
                ContextPath, "History-load failure test", "TestUser")),
            o => o.WithTarget(Mesh.Address)).Should().Within(TimeSpan.FromMilliseconds(SeedMs)).Emit();
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;

        // Warm the cache with a read so Content arrives as a typed MeshThread — otherwise the
        // Update lambda below sees `node.Content is not MeshThread`, short-circuits to a no-op, and
        // the test would silently exercise the healthy path instead (LoadConversationHistoryTest
        // documents the same trap).
        await ThreadFlow.ReadThread(client, threadPath, _ => true,
                timeout: TimeSpan.FromMilliseconds(SeedMs))
            .Should().Within(TimeSpan.FromMilliseconds(SeedMs + WaitMarginMs)).Emit();

        // Stamp phantom cell IDs into Messages. No per-node hub will ever emit content at those
        // paths, so every per-cell read exhausts its budget, the loader's guard refuses to return an
        // empty history, and the round hits exactly the failure #2226 reports.
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            if (node.Content is not MeshThread t) return node;
            return node with { Content = t with { Messages = ImmutableList.Create("phantom-1", "phantom-2") } };
        }).Should().Within(TimeSpan.FromMilliseconds(SeedMs)).Emit();

        // Confirm the phantoms actually landed BEFORE submitting — against a stale snapshot the
        // loader would sail through `cellIds.Count == 0` and the test would assert nothing.
        await ThreadFlow.ReadThread(client, threadPath,
                t => t.Messages.Contains("phantom-1") && t.Messages.Contains("phantom-2"),
                timeout: TimeSpan.FromMilliseconds(SeedMs))
            .Should().Within(TimeSpan.FromMilliseconds(SeedMs + WaitMarginMs)).Emit();

        client.SubmitMessage(threadPath, "what did we conclude earlier?", createdBy: "TestUser");

        // Status=Idle and Summary land in the SAME terminal update, so waiting on both cannot race
        // the write. Messages ≥ 4 = the two phantoms plus this round's user + response cells.
        var thread = await ThreadFlow.ReadThread(client, threadPath,
                t => t.Status == ThreadExecutionStatus.Idle
                     && t.Messages.Count >= 4
                     && !string.IsNullOrEmpty(t.Summary),
                timeout: TimeSpan.FromMilliseconds(RoundMs))
            .Should().Within(TimeSpan.FromMilliseconds(RoundMs + WaitMarginMs)).Emit();

        var cell = await ThreadFlow.ReadMessage(client, threadPath, thread.Messages[^1],
                m => m.Status is ThreadMessageStatus.Completed
                              or ThreadMessageStatus.Cancelled
                              or ThreadMessageStatus.Error,
                timeout: TimeSpan.FromMilliseconds(CellMs))
            .Should().Within(TimeSpan.FromMilliseconds(CellMs + WaitMarginMs)).Emit();

        cell.Status.Should().Be(ThreadMessageStatus.Error,
            "a round that could not load the thread's prior turns must FAIL — settling as Completed "
            + "is what made #2226 a SILENT wrong answer rather than a visible failure");

        var expected = LocalizationCatalog.Get("chat.historyLoadFailed", locale: null);
        cell.Text.Should().Contain(expected,
            "the user must be told why the round stopped, in their own language — the exception "
            + "itself belongs on the LogError, not in the thread");
        thread.Summary.Should().Contain(expected,
            "the thread summary is the thread's own record of why the round failed");

        invocations.Count.Should().Be(0,
            "the agent must NEVER be asked to answer once the history load has failed — calling it "
            + "with an empty history is the entire defect: it produces a confident answer that has "
            + "silently lost every prior turn (#2226)");

        thread.Status.Should().Be(ThreadExecutionStatus.Idle,
            "the round must settle so the thread is usable again — a failed history load must not "
            + "park the thread mid-execution");
    }

    #region Fake LLM

    /// <summary>Instance-scoped invocation counter — no static state, nothing to reset.</summary>
    private sealed class InvocationCounter
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public void Increment() => Interlocked.Increment(ref count);
    }

    private sealed class FakeChatClient(InvocationCounter invocations) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            invocations.Increment();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, FakeResponse)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            invocations.Increment();
            foreach (var word in FakeResponse.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(10, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class FakeChatClientFactory(InvocationCounter invocations) : IChatClientFactory
    {
        public string Name => "FakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new FakeChatClient(invocations),
                instructions: config.Instructions ?? "You are a test assistant.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
