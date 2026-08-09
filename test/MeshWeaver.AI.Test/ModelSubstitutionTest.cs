#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// Pins the HONESTY of the stale-model fallback (GitHub issue #476).
///
/// <para>A round whose pinned model has no usable credential is silently moved onto another model
/// (<c>AgentChatClient.ApplyStaleModelFallback</c>). Three things went wrong with that:</para>
/// <list type="number">
/// <item>The round RECORDED the model that was asked for — <c>request.ModelName</c> was stamped onto
/// the response cell on the streaming, Cancelled and Error paths — while a DIFFERENT model answered.
/// The node then claimed a model that never ran (and the failing round's summary quoted the OTHER
/// model's 429).</item>
/// <item>The swap was surfaced ONLY as an assistant chat message, so a non-interactive round
/// (delegation sub-thread, generator agent, scheduled automation) — which nobody reads — recorded
/// nothing at all.</item>
/// <item>Nothing failed when NO model was usable: the round dead-ended on a factory error and still
/// settled as <c>Completed</c>, i.e. success to any automation reading the node.</item>
/// </list>
///
/// <para>The tests below drive real rounds through <c>ThreadExecution</c> with a scripted chat client
/// (the seam every AI test uses — no mocking of framework interfaces) over a real seeded model
/// catalog: one keyed provider and one KEYLESS provider whose model sorts at a LOWER Order, so a
/// fallback that skipped the health check would prefer it.</para>
/// </summary>
public class ModelSubstitutionTest(ITestOutputHelper output) : AITestBase(output)
{
    private const string TestUser = "rbuergi@systemorph.com";

    private const string KeyedProviderName = "SubstKeyedProvider";
    private const string KeylessProviderName = "SubstKeylessProvider";
    private const string KeyedProviderPath = $"{ModelProviderNodeType.RootNamespace}/{KeyedProviderName}";
    private const string KeylessProviderPath = $"{ModelProviderNodeType.RootNamespace}/{KeylessProviderName}";

    /// <summary>The healthy model: a provider node WITH an ApiKey → <c>HasUsableCredential</c> true.</summary>
    private const string HealthyModel = "subst-healthy-model";
    /// <summary>TokenUsage sanitises non-alphanumerics to '_' when keying the per-model satellite.</summary>
    private const string HealthyUsageKey = "subst_healthy_model";
    /// <summary>
    /// The requested-but-unusable model. Its provider carries NO key, and it deliberately sorts at a
    /// LOWER Order than <see cref="HealthyModel"/> — a fallback ranking on Order alone would pick it.
    /// </summary>
    private const string StaleModel = "subst-stale-model";

    private const int InTokens = 61;
    private const int OutTokens = 29;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory, SubstitutionChatClientFactory>();
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// (a) + (b): the requested model is unusable, the fallback answers — and the round records the
    /// model that ACTUALLY answered plus a machine-readable marker naming the one that was asked
    /// for; the substitute is a model with a usable credential, never the lower-Order keyless one.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task UnusableRequestedModel_RoundRecordsAnsweringModel_AndCarriesRequestedMarker()
    {
        await SeedProvider(KeyedProviderName, apiKey: "sk-subst-476");
        await SeedProvider(KeylessProviderName, apiKey: null);
        await SeedModel(HealthyModel, KeyedProviderPath, KeyedProviderName, order: -1000);
        // Lower Order than the healthy one: if the fallback ranked on Order WITHOUT the usability
        // predicate it would land here — on a model every factory refuses for want of a key.
        await SeedModel(StaleModel, KeylessProviderPath, KeylessProviderName, order: -2000);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        // Warm the resolver's snapshot until the catalog is visible to it. The DEFAULT it computes is
        // the health-checked one — the keyless model outranks it on Order and is still skipped.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.ResolveDefaultModelId())
            .Should().Within(30.Seconds()).Match(id => id == HealthyModel);

        resolver.HasUsableCredential(StaleModel).Should().BeFalse(
            "the requested model's provider carries no ApiKey — every factory throws 'ApiKey is missing' for it");
        resolver.HasUsableCredential(HealthyModel).Should().BeTrue();

        var threadPath = await SeedThread();
        var client = GetClient();
        client.SubmitMessage(threadPath, "hello", modelName: StaleModel, createdBy: TestUser);

        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, 60_000);

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, 30_000);

        cell.ModelName.Should().Be(HealthyModel,
            "the round must record the model that ACTUALLY answered, not the one that was requested (#476)");
        cell.RequestedModelName.Should().Be(StaleModel,
            "the substitution must leave a durable, machine-readable marker on the round — a "
            + "non-interactive caller reads the node, never the chat transcript");
        resolver.HasUsableCredential(cell.ModelName).Should().BeTrue(
            "the fallback must only ever select a model whose credentials actually resolve");

        // …and the same truth flows into token accounting: the per-model satellite is keyed by the
        // model that answered, so its cost is attributed to the right model.
        var usage = await WaitForUsage(threadPath, HealthyUsageKey,
            u => u.InputTokens == InTokens && u.OutputTokens == OutTokens, 30_000);
        usage.Model.Should().Be(HealthyModel);
    }

    /// <summary>
    /// (c): NOTHING resolves — the requested model is unusable and the catalog offers no usable
    /// replacement, so no agent can be built. The round must FAIL with a message that names the
    /// situation, not settle as a success carrying a raw provider error.
    ///
    /// <para>🌍 The round submits under a GERMAN submitter (#948) and asserts the GERMAN catalog
    /// text. Asserting the English entry (<c>locale: null</c>) would pass equally against a
    /// hard-coded English literal AND against a round that lost the submitter's locale — which is
    /// exactly the defect this pins: the submission watcher rebuilds the round's identity from the
    /// submitter rider on the pending message, so the locale has to ride there too.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task NoUsableModelAnywhere_FailsRoundWithSpeakingError()
    {
        // ONLY the keyless provider + its model: nothing in the catalog can serve a round.
        await SeedProvider(KeylessProviderName, apiKey: null);
        await SeedModel(StaleModel, KeylessProviderPath, KeylessProviderName, order: -2000);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        // Warm on the CANDIDATE list (the model is in the snapshot) rather than on the default, which
        // is null both before and after warm-up and would prove nothing.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.ReadTierCandidates())
            .Should().Within(30.Seconds())
            .Match(c => c.Any(m => m.Id == StaleModel));
        resolver.ResolveDefaultModelId().Should().BeNull(
            "the precondition for this test is that NO model in the catalog has a usable credential");

        // 🌍 The assertion below only bites if EN and DE actually differ for this key — otherwise
        // "renders German" and "renders English" are the same string and the test goes vacuous.
        var germanText = LocalizationCatalog.Get("chat.noUsableModel", "de", StaleModel);
        var englishText = LocalizationCatalog.Get("chat.noUsableModel", locale: null, StaleModel);
        germanText.Should().NotBe(englishText,
            "the German and English catalog entries must differ, or asserting the German text "
            + "would pass against an English round and prove nothing (#948)");

        var threadPath = await SeedThread();
        var client = GetClient();

        // Submit as a GERMAN-speaking user. The submitter's identity — locale included — is
        // captured at the submit boundary and rides on the pending message, because the submission
        // watcher's Throttle/Subscribe continuation has no live AccessContext (#948).
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var previousContext = access.CircuitContext;
        access.SetCircuitContext((previousContext ?? TestUsers.Admin) with { Locale = "de" });
        try
        {
            client.SubmitMessage(threadPath, "hello", modelName: StaleModel, createdBy: TestUser);
        }
        finally
        {
            access.SetCircuitContext(previousContext);
        }

        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, 60_000);

        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status is ThreadMessageStatus.Completed or ThreadMessageStatus.Error, 30_000);

        cell.Status.Should().Be(ThreadMessageStatus.Error,
            "a round that no model can serve must FAIL — settling as Completed reads as success to "
            + "every automation that checks the node (#476)");

        // The mechanism, pinned: the submitter's language rides on the user message next to their
        // identity. That rider is what the round-dispatch watcher rebuilds the round's
        // AccessContext from, so if it loses the locale every round speaks English (#948).
        var userCell = await WaitForCell(threadPath, thread.Messages[0], _ => true, 30_000);
        userCell.SubmitterLocale.Should().Be("de",
            "the submit boundary must capture the submitter's language onto the message — it is the "
            + "only carrier that survives the watcher's Throttle/Subscribe hop (#948)");
        // The failure must SPEAK — and speak the SUBMITTER's language: it comes from the
        // localization catalog resolved off the round's own AccessContext.Locale, not a hard-coded
        // English literal, and it names the requested model.
        cell.Summary.Should().Be(germanText,
            "the round's failure text must be the catalog message in the submitter's language — "
            + "the round's AccessContext must carry the submitter's Locale (#948)");
        cell.Summary.Should().Contain(StaleModel, "the message must name the model that was requested");
        cell.Summary.Should().NotContain("ApiKey",
            "the raw provider/factory error belongs in the log, never pasted into the thread (#476)");
        thread.Status.Should().Be(ThreadExecutionStatus.Idle, "the round must settle, never park");
    }

    // ─── Seeding ───

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
        }).Should().Within(30.Seconds()).Emit();

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
        }).Should().Within(30.Seconds()).Emit();

    private async Task<string> SeedThread()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Model Substitution Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = TestUser }
        }).Should().Within(30.Seconds()).Emit();
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

    private async Task<TokenUsage> WaitForUsage(string threadPath, string modelKey, Func<TokenUsage, bool> predicate, int timeoutMs)
        => (await Mesh.GetWorkspace()
            .GetMeshNodeStream($"{threadPath}/{TokenUsageNodeType.SatelliteSegment}/{modelKey}")
            .Select(n => n?.Content as TokenUsage)
            .Where(u => u is not null)
            .Should().Within(TimeSpan.FromMilliseconds(timeoutMs))
            .Match(u => predicate(u!)))!;

    // ─── Scripted chat client ───

    /// <summary>
    /// Streams one text chunk plus a usage report. It sets NO <c>ModelId</c> — the production
    /// condition — so the model on the record can only come from what the client resolved.
    /// </summary>
    private sealed class EchoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ack")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ack");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[]
            {
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = InTokens,
                    OutputTokenCount = OutTokens,
                    TotalTokenCount = InTokens + OutTokens
                })
            });
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Serves both catalog ids, and — like every real factory — REFUSES a model whose credentials
    /// don't resolve ("ApiKey is missing for model 'X'"). That refusal is what makes an unusable
    /// selection produce zero agents, which is the state the round-failure path keys on.
    /// </summary>
    private sealed class SubstitutionChatClientFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
    {
        public override string Name => "SubstitutionFactory";
        public override IReadOnlyList<string> Models => [HealthyModel, StaleModel];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
        {
            var resolver = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
            if (resolver is not null && !resolver.HasUsableCredential(CurrentModelName))
                throw new InvalidOperationException(
                    $"ApiKey is missing for model '{CurrentModelName ?? "(none selected)"}'");
            return new EchoChatClient();
        }
    }
}
