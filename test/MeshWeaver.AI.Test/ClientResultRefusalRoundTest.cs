#pragma warning disable CS1591

using System;
using System.Collections.Generic;
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
/// Pins the two upstream refusals GitHub issue #2233 reports — <b>HTTP 402</b> (the OpenRouter
/// credit balance is below the round's <c>max_tokens</c>) and <b>HTTP 404</b> (the configured model
/// id does not exist at the endpoint) — end to end, as a real round.
///
/// <para><c>ProviderRefusalRoundTest</c> is the sibling for 429 and covers the model-substitution
/// half of #476. This one exists because those two statuses arrive wearing a DIFFERENT banner:
/// <c>System.ClientModel</c> renders <c>"HTTP 402 (: )"</c>, not Azure's <c>"Status: 402 (…)"</c>.
/// <c>ProviderFailureClassifier</c> knew only the Azure form, so every OpenAI/OpenRouter refusal —
/// i.e. every refusal the portal actually receives — fell through unclassified and pasted the
/// provider's raw English body into the thread. The unit-level proof is in
/// <c>ProviderFailureClassifierTest</c>; this test proves the classification survives all the way
/// to what the user reads and to the cell's terminal Status.</para>
///
/// <para>Three things must hold, and each was broken before #2233:
/// <list type="number">
///   <item>the round FAILS — <c>Status = Error</c>, never Completed, never parked mid-stream;</item>
///   <item>the text is the LOCALIZED condition for that status, so a German viewer reads German
///     and a 402 is not dressed up as "submit again later" (it is not transient);</item>
///   <item>the provider's raw body — credit figures, a top-up URL — stays in the log.</item>
/// </list></para>
/// </summary>
public class ClientResultRefusalRoundTest(ITestOutputHelper output) : AITestBase(output)
{
    private const string TestUser = "rbuergi@systemorph.com";

    private const string ProviderName = "ClientResultRefusalProvider";
    private const string ProviderPath = $"{ModelProviderNodeType.RootNamespace}/{ProviderName}";

    /// <summary>
    /// The model the round asks for AND runs on. It has a usable credential, so nothing substitutes
    /// it away — the failure comes purely from the provider's answer, which is the case #2233
    /// describes (an account that ran out of credit mid-life, not a misconfigured key).
    /// </summary>
    private const string RefusingModel = "clientresult-refusing-model";

    /// <summary>
    /// Verbatim from production (<c>Admin/_LogIncident/11973e8dfa3d0711</c>, 7 of 8 samples). The
    /// second line is the provider's own English prose — actionable to an operator, meaningless to
    /// the end user, and untranslatable. It must not reach the thread.
    /// </summary>
    private const string CreditExhaustedDump =
        """
        HTTP 402 (: )

        This request requires more credits, or fewer max_tokens. You requested up to 65536 tokens, but can only afford 6383. To increase, visit https://openrouter.ai/settings/credits and add more credits
        """;

    /// <summary>The 8th sample: a model id the endpoint does not serve.</summary>
    private const string ModelNotFoundDump =
        """
        HTTP 404 (: 404)

        Resource not found
        """;

    // ─── Timeout budget (same discipline as ProviderRefusalRoundTest) ───
    // Each bound detects a BROKEN fixture, not slowness: the scripted client throws on the first
    // streaming call, so a healthy case settles in well under a second. The ceiling is DERIVED from
    // the sum so an inner wait — the one whose message names the stage — always fires first.

    private const int SeedMs = 10_000;
    private const int SeedCount = 3;
    private const int ResolverWarmupMs = 15_000;
    private const int RoundMs = 20_000;
    private const int CellMs = 5_000;
    private const int InnerBudgetMs = SeedMs * SeedCount + ResolverWarmupMs + RoundMs + CellMs;
    private const int MethodTimeoutMs = InnerBudgetMs * 4 / 3;

    /// <summary>
    /// Set per case before the submit; the scripted client reads it when the round reaches the
    /// provider. An instance field of the test class (one mesh per theory case), never static.
    /// </summary>
    private string refusalDump = CreditExhaustedDump;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(sp =>
                    new RefusingChatClientFactory(
                        sp.GetRequiredService<IMessageHub>(), () => refusalDump));
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// ⏱️ Ceiling = <c>InnerBudgetMs × 4/3</c> (seeds 30s + warm-up 15s + round 20s + cell 5s = 70s
    /// → 93s), derived rather than written as a literal so it can never drop below the sum of the
    /// stages it must outlive.
    ///
    /// <para><paramref name="leakedFragments"/> is the discriminating half of the assertion: an
    /// implementation that merely wrapped <c>ex.Message</c> in a friendlier sentence would still
    /// pass the "contains the localized condition" check while shipping the provider's body — which
    /// is precisely the defect. Naming the fragments makes that impossible.</para>
    /// </summary>
    [Theory(Timeout = MethodTimeoutMs)]
    [InlineData(402, "chat.modelQuotaExhausted", CreditExhaustedDump,
        new[] { "openrouter.ai/settings/credits", "max_tokens", "65536", "afford" })]
    [InlineData(404, "chat.modelNotFound", ModelNotFoundDump,
        new[] { "Resource not found" })]
    public async Task UpstreamRefusal_FailsTheRoundLegibly(
        int status, string expectedKey, string dump, string[] leakedFragments)
    {
        refusalDump = dump;

        await SeedProvider();
        await SeedModel();

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.HasUsableCredential(RefusingModel))
            .Should().Within(TimeSpan.FromMilliseconds(ResolverWarmupMs))
            .Match(usable => usable);

        var threadPath = await SeedThread();
        GetClient().SubmitMessage(threadPath, "hello", modelName: RefusingModel, createdBy: TestUser);

        // Status=Idle and Summary are written in the SAME terminal update, so waiting on both keeps
        // the assertion from racing the write.
        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle
                 && t.Messages.Count >= 2
                 && !string.IsNullOrEmpty(t.Summary), RoundMs);
        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status is ThreadMessageStatus.Completed or ThreadMessageStatus.Error, CellMs);

        cell.Status.Should().Be(ThreadMessageStatus.Error,
            $"an HTTP {status} the provider refused the round with must FAIL it — before #2233 this "
            + "exception escaped as an unclassified fault and the user watched a spinner go nowhere");

        var expectedCondition = LocalizationCatalog.Get(expectedKey, locale: null, RefusingModel);
        cell.Text.Should().Contain(expectedCondition,
            "the failure a user reads must come from the localization catalogue — the provider's "
            + "own body is English-only and renders English for a German viewer (#2233)");
        thread.Summary.Should().Contain(expectedCondition,
            "the thread summary is the thread's own record of why it failed");

        foreach (var leak in leakedFragments)
        {
            cell.Text.Should().NotContain(leak,
                "the raw provider body belongs in the log, never pasted into the thread (#2233)");
            thread.Summary.Should().NotContain(leak,
                "the raw provider body belongs in the log, never pasted into the thread (#2233)");
        }

        thread.Status.Should().Be(ThreadExecutionStatus.Idle,
            "the round must SETTLE on a refusal — an escaping exception used to leave it parked");
        cell.ModelName.Should().Be(RefusingModel,
            "the record attributes the round to the model that actually served it");
    }

    // ─── Seeding ───

    private async Task SeedProvider() =>
        await NodeFactory.CreateNode(new MeshNode(ProviderName, ModelProviderNodeType.RootNamespace)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = ProviderName,
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = ProviderName,
                ApiKey = "sk-clientresult-2233",
                Endpoint = "https://example.invalid/v1",
                Label = ProviderName,
                CreatedAt = DateTimeOffset.UtcNow
            }
        }).Should().Within(TimeSpan.FromMilliseconds(SeedMs)).Emit();

    private async Task SeedModel() =>
        await NodeFactory.CreateNode(new MeshNode(RefusingModel, ProviderPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = RefusingModel,
            State = MeshNodeState.Active,
            Order = -1000,
            Content = new ModelDefinition
            {
                Id = RefusingModel,
                Provider = ProviderName,
                ProviderRef = ProviderPath,
                Order = -1000
            }
        }).Should().Within(TimeSpan.FromMilliseconds(SeedMs)).Emit();

    private async Task<string> SeedThread()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Client Result Refusal Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
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
    /// Constructs fine — its credential resolves — and then refuses the round exactly as the real
    /// transport does: the failure surfaces only once streaming starts, several frames below the
    /// caller, which is why <c>ThreadExecution</c> sees it wrapped.
    /// </summary>
    private sealed class RefusingChatClient(Func<string> dump) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(dump());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException(dump());
#pragma warning disable CS0162 // Unreachable — required for the iterator's yield type inference.
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class RefusingChatClientFactory(IMessageHub hub, Func<string> dump)
        : ChatClientAgentFactory(hub)
    {
        public override string Name => "ClientResultRefusingFactory";
        public override IReadOnlyList<string> Models => [RefusingModel];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
        {
            var resolver = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
            if (resolver is not null && !resolver.HasUsableCredential(CurrentModelName))
                throw new InvalidOperationException(
                    $"ApiKey is missing for model '{CurrentModelName ?? "(none selected)"}'");
            return new RefusingChatClient(dump);
        }
    }
}
