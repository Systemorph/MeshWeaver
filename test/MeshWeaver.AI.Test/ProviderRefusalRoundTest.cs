#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
/// Pins what a round LOOKS LIKE when the provider refuses it (GitHub issue #476, code half).
///
/// <para><c>ModelSubstitutionTest</c> already covers the case where nothing can be resolved at all.
/// This one covers the case the portal actually hits: the fallback DOES find a model with usable
/// credentials, the round runs on it — and the deployment is out of quota. The credential check
/// that guards the swap cannot see that; only the provider's answer can.</para>
///
/// <para>What used to happen: <c>ThreadExecution</c> pasted <c>ex.Message</c> into the response
/// cell's Text and Summary. For an Azure / System.ClientModel transport failure that message is the
/// status line, the response body AND the complete HTTP header block — so the thread's own record of
/// why it failed was an unreadable, English-only dump, and because the round had been silently moved
/// onto a substitute, the dump named a model the user never picked with nothing saying why.</para>
///
/// <para>What must happen: the round fails (never "Completed"), and it fails LEGIBLY — the localized
/// condition naming the model that actually served, plus, because this round was substituted, one
/// sentence naming both models. The raw provider text belongs to the log alone.</para>
/// </summary>
public class ProviderRefusalRoundTest(ITestOutputHelper output) : AITestBase(output)
{
    private const string TestUser = "rbuergi@systemorph.com";

    private const string KeyedProviderName = "RefusalKeyedProvider";
    private const string KeylessProviderName = "RefusalKeylessProvider";
    private const string KeyedProviderPath = $"{ModelProviderNodeType.RootNamespace}/{KeyedProviderName}";
    private const string KeylessProviderPath = $"{ModelProviderNodeType.RootNamespace}/{KeylessProviderName}";

    /// <summary>Has a key, so the fallback selects it — and then its deployment answers 429.</summary>
    private const string ThrottledModel = "refusal-throttled-model";
    /// <summary>The requested model: no key at all, so <c>ApplyStaleModelFallback</c> swaps it away.</summary>
    private const string StaleModel = "refusal-stale-model";

    // ─── Timeout budget ───
    // Every bound below detects a BROKEN fixture; none is a performance budget (the healthy test
    // settles in well under a second). They are sized so their SUM stays under the method ceiling,
    // which is what keeps an inner wait — the one carrying a message that names the stage — the
    // thing that fires. See the budget note on the test method.

    /// <summary>
    /// One seeded node reaching the workspace. In-memory write, no IO. Five of these run before any
    /// assertion, so they are part of the budget, not free setup.
    /// </summary>
    private const int SeedMs = 10_000;

    /// <summary>Number of <see cref="SeedMs"/>-bounded seed calls the test makes before it waits.</summary>
    private const int SeedCount = 5;

    /// <summary>
    /// Warm-up of an in-memory synced query — no network, no IO. Observed sub-second; this is a
    /// loaded-CI allowance, not an expectation.
    /// </summary>
    private const int ResolverWarmupMs = 15_000;

    /// <summary>
    /// The round itself. Bounded by something real: the scripted client throws on the FIRST
    /// streaming call, so this is mesh plumbing only — submit → watcher → round → terminal write.
    /// There is no model call to wait on, which is why it does not need a "however long a round
    /// takes" allowance.
    /// </summary>
    private const int RoundMs = 20_000;

    /// <summary>
    /// Pure backstop. The thread's terminal write (Status=Idle + Summary) happens INSIDE the
    /// response cell push's completion handler, so by the time the thread wait above succeeds the
    /// cell is already terminal — this wait cannot legitimately block. Kept as a bounded read rather
    /// than an unbounded one so a broken ordering fails here, loudly, instead of hanging.
    /// </summary>
    private const int CellMs = 5_000;

    /// <summary>Worst case if EVERY bounded stage in the method spends its full allowance.</summary>
    private const int InnerBudgetMs = SeedMs * SeedCount + ResolverWarmupMs + RoundMs + CellMs;

    /// <summary>
    /// The method ceiling, DERIVED from <see cref="InnerBudgetMs"/> rather than written as a
    /// literal, so the "inner waits fire first" invariant is structural: change any constant above
    /// and the ceiling moves with it, and it can never silently drop below the sum it must exceed.
    /// The 4/3 factor is the margin — 33% of whatever the stages currently add up to.
    /// </summary>
    private const int MethodTimeoutMs = InnerBudgetMs * 4 / 3;

    /// <summary>
    /// A verbatim-shaped Azure / System.ClientModel rate-limit failure, headers and all — the exact
    /// payload #476 reports finding pasted into the thread.
    /// </summary>
    private const string RateLimitDump =
        """
        Your requests to DeepSeek-V4-Flash for DeepSeek-V4-Flash in swedencentral have exceeded rate limit.
        Status: 429 (Too Many Requests)
        ErrorCode: RateLimitReached

        Content:
        {"error":{"code":"RateLimitReached","message":"Rate limit is exceeded. Try again in 54 seconds."}}

        Headers:
        Content-Length: 210
        x-ms-client-request-id: 1f0c1f4c-0a1e-4f8a-9f2f-2b0a3f6a1234
        x-ratelimit-remaining-requests: 0
        Retry-After: 54
        """;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory, RefusingChatClientFactory>();
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// ⏱️ TIMEOUT BUDGET — the ceiling is a BACKSTOP; an inner wait must always be what fires.
    /// xUnit's method-timeout abort carries no assertion message, so if it wins the race you lose
    /// the single fact worth having: WHICH stage hung. Every bounded stage inside this method is
    /// therefore counted, including the seeds — they run before the first assertion, so they spend
    /// the same budget:
    ///
    /// <code>
    ///   seeds   5 × 10s = 50s   (SeedMs × SeedCount)
    ///   warm-up          15s    (ResolverWarmupMs)
    ///   round            20s    (RoundMs)
    ///   cell              5s    (CellMs)
    ///   ────────────────────
    ///   worst case       90s    (InnerBudgetMs)
    ///   ceiling         120s    (MethodTimeoutMs = InnerBudgetMs × 4/3) → 30s margin
    /// </code>
    ///
    /// <para>🚨 Those figures restate the constants for readability and are the ONE part of this
    /// that can drift — the constants are authoritative. If you change a stage bound, update this
    /// table or delete it; a table claiming numbers the code does not have is the exact failure
    /// this derived ceiling exists to prevent.</para>
    ///
    /// <para>The ceiling is <b>derived</b> from that sum, not written as a literal, so it cannot
    /// drift below it. It exceeds the repo's 30s default because of the sum, not because anything
    /// here is slow: the healthy test settles in under a second, and every constant above is a
    /// broken-fixture detector sized far beyond the observed time. To lower the ceiling, lower the
    /// stage constants — the arithmetic then does it for you.</para>
    /// </summary>
    [Fact(Timeout = MethodTimeoutMs)]
    public async Task ProviderRefusesSubstituteModel_RoundFailsLegibly_NamingBothModels()
    {
        await SeedProvider(KeyedProviderName, apiKey: "sk-refusal-476");
        await SeedProvider(KeylessProviderName, apiKey: null);
        await SeedModel(ThrottledModel, KeyedProviderPath, KeyedProviderName, order: -1000);
        await SeedModel(StaleModel, KeylessProviderPath, KeylessProviderName, order: -2000);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.ResolveDefaultModelId())
            .Should().Within(TimeSpan.FromMilliseconds(ResolverWarmupMs))
            .Match(id => id == ThrottledModel);

        // The precondition that makes this test the INTERESTING case: the substitute passes every
        // check we can make locally. Only the provider's answer reveals it cannot serve.
        resolver.HasUsableCredential(ThrottledModel).Should().BeTrue(
            "the fallback target has a real credential — which is exactly why a credential check "
            + "cannot prevent this failure, and why the failure has to READ well (#476)");

        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "hello", modelName: StaleModel, createdBy: TestUser);

        // The thread's Summary is the field #476 reports the raw dump landing in, and it is written
        // in the SAME terminal update as Status=Idle — wait on both so the assertion cannot race it.
        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle
                 && t.Messages.Count >= 2
                 && !string.IsNullOrEmpty(t.Summary), RoundMs);
        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status is ThreadMessageStatus.Completed or ThreadMessageStatus.Error, CellMs);

        cell.Status.Should().Be(ThreadMessageStatus.Error,
            "a round the provider refused must FAIL — settling as Completed reads as success to "
            + "every automation that checks the node");

        // ── The failure must be the localized CONDITION, naming the model that actually served ──
        var expectedCondition = LocalizationCatalog.Get("chat.modelRateLimited", locale: null, ThrottledModel);
        cell.Text.Should().Contain(expectedCondition,
            "the round's failure text must come from the localization catalogue — a hard-coded "
            + "English sentence renders English for a German viewer");
        thread.Summary.Should().Contain(expectedCondition,
            "the thread summary is where #476 found the raw provider dump");

        // ── …and, because this round was substituted, say so in words, once ──
        var expectedNote = LocalizationCatalog.Get(
            "chat.modelSubstitutionNote", locale: null, StaleModel, ThrottledModel);
        cell.Text.Should().Contain(expectedNote,
            "the failure names a model the user never picked; without this sentence the thread is "
            + "inexplicable to the person reading it (#476)");
        thread.Summary.Should().Contain(expectedNote);

        // ── The raw transport dump must NOT be in the thread ──
        foreach (var leak in new[]
                 {
                     "Headers:", "x-ratelimit-remaining-requests", "x-ms-client-request-id",
                     "Retry-After", "ErrorCode: RateLimitReached"
                 })
        {
            cell.Text.Should().NotContain(leak,
                "the raw transport dump belongs in the log, never pasted into the thread (#476)");
            thread.Summary.Should().NotContain(leak,
                "the raw transport dump belongs in the log, never pasted into the thread (#476)");
        }

        // ── The record still attributes the round to the model that actually ran (#476, part a) ──
        cell.ModelName.Should().Be(ThrottledModel,
            "the round must record the model that actually served it, not the one requested");
        cell.RequestedModelName.Should().Be(StaleModel,
            "the machine-readable substitution marker must survive onto the FAILED round too — a "
            + "non-interactive caller reads the node, never the prose");

        thread.Status.Should().Be(ThreadExecutionStatus.Idle, "the round must settle, never park");
    }

    // ─── Seeding (same shape as ModelSubstitutionTest) ───

    private async Task SeedProvider(string name, string? apiKey) =>
        await NodeFactory.CreateNode(new MeshNode(name, ModelProviderNodeType.RootNamespace)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = name,
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = name,
                ApiKey = apiKey,
                Endpoint = "https://example.invalid/v1",
                Label = name,
                CreatedAt = DateTimeOffset.UtcNow
            }
        }).Should().Within(TimeSpan.FromMilliseconds(SeedMs)).Emit();

    private async Task SeedModel(string id, string providerPath, string providerName, int order) =>
        await NodeFactory.CreateNode(new MeshNode(id, providerPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = id,
            State = MeshNodeState.Active,
            Order = order,
            Content = new ModelDefinition
            {
                Id = id,
                Provider = providerName,
                ProviderRef = providerPath,
                Order = order
            }
        }).Should().Within(TimeSpan.FromMilliseconds(SeedMs)).Emit();

    private async Task<string> SeedThread()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Provider Refusal Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = TestPartition,
            Content = new MeshThread { CreatedBy = TestUser }
        }).Should().Within(TimeSpan.FromMilliseconds(SeedMs)).Emit();
        return threadPath;
    }

    // ─── Waits ───

    private async Task<MeshThread> WaitForThread(string threadPath, Func<MeshThread, bool> predicate, int timeoutMs)
        => (await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n?.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(t => predicate(t!)))!;

    private async Task<ThreadMessage> WaitForCell(string threadPath, string cellId, Func<ThreadMessage, bool> predicate, int timeoutMs)
        => (await Mesh.GetWorkspace().GetMeshNodeStream($"{threadPath}/{cellId}")
            .Select(n => n?.Content as ThreadMessage)
            .Where(m => m is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(m => predicate(m!)))!;

    // ─── Scripted chat client ───

    /// <summary>
    /// Creates fine (its credentials resolve — that is the point) and then refuses the round exactly
    /// as a throttled deployment does: the failure appears only once streaming starts.
    /// </summary>
    private sealed class ThrottledChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(RateLimitDump);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException(RateLimitDump);
#pragma warning disable CS0162 // Unreachable — required for the iterator's yield type inference.
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Refuses a model with no resolvable credential (like every real factory), and hands back a
    /// client that dies on the provider for the one that does.
    /// </summary>
    private sealed class RefusingChatClientFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
    {
        public override string Name => "RefusingFactory";
        public override IReadOnlyList<string> Models => [ThrottledModel, StaleModel];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
        {
            var resolver = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
            if (resolver is not null && !resolver.HasUsableCredential(CurrentModelName))
                throw new InvalidOperationException(
                    $"ApiKey is missing for model '{CurrentModelName ?? "(none selected)"}'");
            return new ThrottledChatClient();
        }
    }
}
