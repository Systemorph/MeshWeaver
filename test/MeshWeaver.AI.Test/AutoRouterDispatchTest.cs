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
/// <b>Auto must actually dispatch.</b> Auto is the DEFAULT selection for a new thread, and it is a
/// router: it holds no endpoint and no key and can never serve a round itself. So the one thing that
/// must be true of it is that a round carrying it runs on a REAL model — never on "auto", never on
/// nothing.
///
/// <para>That was the gap this pins. <c>IsRouter</c> existed and was read only to EXCLUDE the entry
/// from selection; nothing ever turned an Auto selection into a model, and the flag's own
/// documentation said Auto was reachable "only ever EXPLICITLY, by a user picking it" — the opposite
/// of being the default. A round on Auto therefore reached a factory with a model id no provider
/// serves.</para>
///
/// <para>Drives a real round through <c>ThreadExecution</c> with a scripted chat client (the seam
/// every AI test uses — no mocking of framework interfaces) over a real seeded catalog: one keyed
/// provider, a pinned deployment default, and a <c>coding</c>-labelled model that deliberately sorts
/// LAST so "it dispatched on the tier" can never be confused with "it took the default".</para>
/// </summary>
public class AutoRouterDispatchTest(ITestOutputHelper output) : AITestBase(output)
{
    private const string TestUser = "rbuergi@systemorph.com";

    private const string ProviderName = "AutoDispatchProvider";
    private const string ProviderPath = $"{ModelProviderNodeType.RootNamespace}/{ProviderName}";

    /// <summary>The pinned deployment default: lowest Order, keyed provider.</summary>
    private const string DefaultModel = "auto-default-model";

    /// <summary>
    /// The <c>coding</c>-labelled model. Deliberately sorts LAST — a dispatch that ignored the tier
    /// and took the deployment default would land on <see cref="DefaultModel"/>, so landing here can
    /// only mean the tier was honoured.
    /// </summary>
    private const string CodingModel = "auto-coding-model";

    /// <summary>
    /// A ROUTER parked on the KEYED provider — i.e. one for which <c>HasUsableCredential</c> is TRUE.
    /// The shipped Auto is keyless, so only this shape distinguishes a deliberate router dispatch
    /// from the older "the selected model is unusable, seed a working one" fallback.
    /// </summary>
    private const string KeyedRouterModel = "auto-router-with-key";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory, AutoDispatchChatClientFactory>();
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// 🧊 <b>Router identity must not wait for the catalog.</b> The credential snapshot is lazily
    /// warmed, and both callers of <see cref="ChatClientCredentialResolver.IsRouterSelection"/> read
    /// <c>false</c> as "an ordinary model". Answering off the snapshot alone therefore mis-answers for
    /// Auto during the window before the first emission lands — which is exactly when a fresh circuit
    /// initialises a composer, so <c>ValidMasterModel</c> would find no usable credential for Auto (it
    /// deliberately has none) and <b>rewrite the user's stored Auto selection to a concrete model</b>.
    /// A persisted, lossy change caused by nothing but timing.
    ///
    /// <para>This test never warms the resolver — it asserts the snapshot IS cold first, so it cannot
    /// pass vacuously against a catalog that happened to load — and then requires both persisted forms
    /// of the router to be recognised anyway. It fails against a snapshot-only implementation.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void RouterIsRecognisedAgainstAColdCatalog()
    {
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();

        // Precondition: deliberately NO EnsureSubscription() — nothing has warmed the snapshot.
        resolver.ReadTierCandidates().Should().BeEmpty(
            "this test is about the pre-warm window; if the catalog is already readable it proves nothing");
        resolver.HasReadableCatalog.Should().BeFalse(
            "a cold snapshot cannot tell 'gone' from 'not loaded yet' — which is why callers that "
            + "validate a STORED selection must keep it rather than reset it (ValidMasterModel)");

        resolver.IsRouterSelection(LanguageModelNodeType.RouterModelId).Should().BeTrue(
            "the bare id is what AgentChatClient carries once ResolveModelId has run");
        resolver.IsRouterSelection(LanguageModelNodeType.RouterPath).Should().BeTrue(
            "the node PATH is what the composer persists, and what it hands back before the catalog "
            + "is readable enough for ResolveModelId to shorten it");

        // …and the static rule stays narrow: it must not wave through anything that merely looks
        // similar, or a real model would be dispatched away instead of served.
        resolver.IsRouterSelection("automatic").Should().BeFalse();
        resolver.IsRouterSelection("Provider/Auto").Should().BeFalse();
        resolver.IsRouterSelection("some-other/auto-model").Should().BeFalse();
        resolver.IsRouterSelection(null).Should().BeFalse();
        resolver.IsRouterSelection("").Should().BeFalse();
    }

    /// <summary>
    /// The round-level contract: submit with the SHIPPED Auto selected — exactly what the composer
    /// persists for a new thread — and the round records a real, usable model. Asserting on the
    /// RECORDED model (not on a log line) is the point: that is the same value token accounting and
    /// every per-model roll-up is keyed by, so a round that silently ran on something else shows up
    /// here.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AutoSelected_RoundRunsOnARealModel_NeverOnAuto()
    {
        var resolver = await SeedCatalogAndWarm();

        resolver.IsRouterSelection(LanguageModelNodeType.RouterModelId).Should().BeTrue(
            "the precondition for this test is that 'auto' IS a router in the live catalog");
        resolver.IsRouterSelection(LanguageModelNodeType.RouterPath).Should().BeTrue(
            "the composer persists the node PATH — the router check must recognise that form too");

        var threadPath = await SeedThread();
        var client = GetClient();
        // Submit exactly what the composer persists for the default selection of a new thread.
        client.SubmitMessage(threadPath, "hello",
            modelName: LanguageModelNodeType.RouterPath, createdBy: TestUser);

        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, 60_000);
        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, 30_000);

        cell.ModelName.Should().NotBeNullOrEmpty("Auto must resolve to something — never to nothing");
        cell.ModelName.Should().NotBe(LanguageModelNodeType.RouterModelId,
            "Auto dispatches to a real model; resolving Auto to Auto hands the round to something "
            + "that cannot answer it");
        resolver.IsRouterSelection(cell.ModelName).Should().BeFalse(
            "the model that served must not itself be a router");
        resolver.HasUsableCredential(cell.ModelName).Should().BeTrue(
            "the dispatch target is health-checked — it can only ever be a model whose credentials resolve");
        cell.ModelName.Should().Be(DefaultModel,
            "with no tier declared, Auto's rule is the deployment default");
        thread.Status.Should().Be(ThreadExecutionStatus.Idle, "the round must settle, never park");
    }

    /// <summary>
    /// 🎯 The assertion that separates <b>dispatch</b> from the pre-existing stale-model fallback,
    /// and the reason the router check runs BEFORE the usability check.
    ///
    /// <para>The shipped Auto node carries no key, so a round on it also happens to satisfy the OLD
    /// "selected model is unusable → seed a working one" path — meaning
    /// <see cref="AutoSelected_RoundRunsOnARealModel_NeverOnAuto"/> alone cannot tell the two apart.
    /// A router parked on a KEYED provider can: <c>HasUsableCredential</c> is true for it, so the
    /// unusable-model path returns early and the round would be handed to a factory with the model id
    /// <c>auto</c> — which no provider serves. Only a deliberate router check, taken first and
    /// independently of usability, produces a completed round here.</para>
    ///
    /// <para>Verified to bite: with the router check disabled (<c>isRouter = false</c>, the
    /// pre-change behaviour) this test fails — the round never reaches Completed because no factory
    /// accepts the router's id.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARouterOnAKeyedProviderIsStillDispatched_NotServed()
    {
        var resolver = await SeedCatalogAndWarm();
        // A router node that DOES resolve to a key — the shape the usability check would wave through.
        await SeedModel(KeyedRouterModel, order: -900, tier: null, isRouter: true);
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.ReadTierCandidates())
            .Should().Within(30.Seconds()).Match(c => c.Any(m => m.Id == KeyedRouterModel));

        resolver.HasUsableCredential(KeyedRouterModel).Should().BeTrue(
            "the precondition: this router's provider carries a key, so the unusable-model path "
            + "would return early and never dispatch it");
        resolver.IsRouterSelection(KeyedRouterModel).Should().BeTrue();

        var threadPath = await SeedThread();
        GetClient().SubmitMessage(threadPath, "hello", modelName: KeyedRouterModel, createdBy: TestUser);

        var thread = await WaitForThread(threadPath,
            t => t.Status == ThreadExecutionStatus.Idle && t.Messages.Count >= 2, 60_000);
        var cell = await WaitForCell(threadPath, thread.Messages[^1],
            m => m.Status == ThreadMessageStatus.Completed, 30_000);

        cell.ModelName.Should().Be(DefaultModel,
            "a router names no wire model — 'it has a key' is the wrong reason to let it reach a factory");
        cell.RequestedModelName.Should().Be(KeyedRouterModel,
            "the dispatch must leave the durable requested→effective marker the operator reads (#476)");
    }

    /// <summary>
    /// The resolution chain Auto dispatches THROUGH, read off the live snapshot rather than a
    /// hand-built candidate list: a declared tier beats the pinned deployment default, an
    /// unpopulated tier falls through to it, and NOTHING — not the default, not a tier, not an
    /// unknown label — ever resolves to the router.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TierResolutionOverTheLiveCatalog_NeverYieldsTheRouter_AndAlwaysYieldsAModel()
    {
        var resolver = await SeedCatalogAndWarm();

        // The router is in the catalog and sorts first — and is still not the default.
        resolver.ReadTierCandidates().Should().Contain(c => c.IsRouter,
            "the shipped Auto node must be visible to the resolver");
        resolver.ResolveDefaultModelId().Should().Be(DefaultModel);

        // A declared tier beats the pinned default — the coding model sorts LAST, so this can only
        // be the tier taking effect.
        var coding = resolver.ResolveForTier(ModelTierDefaults.Coding);
        coding.ModelId.Should().Be(CodingModel);
        coding.Source.Should().Be(ModelTierSource.Label);
        coding.IsUnpopulatedTierFallback.Should().BeFalse();

        // An unpopulated tier degrades to the default AND says so, so the operator can be told.
        var reasoning = resolver.ResolveForTier(ModelTierDefaults.Reasoning);
        reasoning.ModelId.Should().Be(DefaultModel);
        reasoning.IsUnpopulatedTierFallback.Should().BeTrue(
            "a tier nobody carries is designed behaviour, but a substitution the operator cannot see "
            + "is the #476 defect");

        // Every shape terminates in a usable, non-router model.
        foreach (var label in new[] { null, "", "coding", "heavy", "utility", "gigantic" })
        {
            var resolution = resolver.ResolveForTier(label);
            resolution.IsExhausted.Should().BeFalse($"'{label ?? "(null)"}' must still reach a model");
            resolver.IsRouterSelection(resolution.ModelId).Should().BeFalse(
                $"'{label ?? "(null)"}' must never resolve to the router");
            resolver.HasUsableCredential(resolution.ModelId).Should().BeTrue(
                $"'{label ?? "(null)"}' must reach a model whose credentials resolve");
        }

        // The deployment's own tier registry is live — the shipped tiers arrived as nodes.
        resolver.ReadTiers().Select(t => t.Id).Should().Contain(ModelTierDefaults.Coding);
    }

    // ─── Seeding ───

    private async Task<ChatClientCredentialResolver> SeedCatalogAndWarm()
    {
        await SeedProvider();
        await SeedModel(DefaultModel, order: -1000, tier: null);
        await SeedModel(CodingModel, order: 500, tier: ModelTierDefaults.Coding);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        // Warm on the CANDIDATE list rather than on the default: both seeded models plus the shipped
        // router have to be visible before any assertion about ranking means anything.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.ReadTierCandidates())
            .Should().Within(30.Seconds())
            .Match(c => c.Any(m => m.Id == DefaultModel)
                        && c.Any(m => m.Id == CodingModel)
                        && c.Any(m => m.IsRouter));
        return resolver;
    }

    private async Task SeedProvider() =>
        await NodeFactory.CreateNode(new MeshNode(ProviderName, ModelProviderNodeType.RootNamespace)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = ProviderName,
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = ProviderName,
                ApiKey = "sk-auto-dispatch",
                Endpoint = "https://example.invalid/v1",
                Label = ProviderName,
                CreatedAt = DateTimeOffset.UtcNow
            }
        }).Should().Within(30.Seconds()).Emit();

    private async Task SeedModel(string id, int order, string? tier, bool isRouter = false) =>
        await NodeFactory.CreateNode(new MeshNode(id, ProviderPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = id,
            State = MeshNodeState.Active,
            Order = order,
            Content = new ModelDefinition
            {
                Id = id,
                Provider = ProviderName,
                ProviderRef = ProviderPath,
                Order = order,
                Tier = tier,
                IsRouter = isRouter ? true : null
            }
        }).Should().Within(30.Seconds()).Emit();

    private async Task<string> SeedThread()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Auto Dispatch Thread {threadId}",
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

    // ─── Scripted chat client ───

    /// <summary>
    /// Streams one text chunk. It sets NO <c>ModelId</c> — the production condition — so the model
    /// on the record can only come from what the client resolved.
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
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Serves the two seeded catalog ids and, like every real factory, REFUSES anything else —
    /// including the router's own id. That refusal is what makes "Auto reached a factory undispatched"
    /// a visible failure rather than a silently-served round.
    /// </summary>
    private sealed class AutoDispatchChatClientFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
    {
        public override string Name => "AutoDispatchFactory";
        public override IReadOnlyList<string> Models => [DefaultModel, CodingModel];
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
