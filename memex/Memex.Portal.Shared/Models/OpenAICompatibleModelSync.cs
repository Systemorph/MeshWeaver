using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Models;

/// <summary>
/// Keeps the <c>OpenAICompatible</c> provider's <c>LanguageModel</c> catalog in sync with the models
/// actually installed on the endpoint it points at — the local Ollama use case: pull a model
/// (<c>ollama pull …</c>) and it shows up in the picker without hand-editing
/// <c>OpenAICompatible:Models[]</c>.
///
/// <para>Opt-in, off by default: does nothing unless <c>OpenAICompatible:DiscoverModels=true</c> AND
/// <c>OpenAICompatible:Endpoint</c> is set. When enabled, leave <c>OpenAICompatible:Models[]</c> empty —
/// discovery OWNS this provider's child set (the static <see cref="BuiltInLanguageModelProvider"/> still
/// seeds the parent <c>Provider/OpenAICompatible</c> node with the endpoint/key; only the model children
/// come from here).</para>
///
/// <para>🚨 Reactive end-to-end, no ambient identity (a hosted service, not a request):
/// <list type="bullet">
///   <item>The model list is fetched via <see cref="ProviderModelLister"/> — its HTTP leaf runs inside
///     the <c>IIoPool</c>, never on a hub thread.</item>
///   <item>Every read AND write is wrapped in <see cref="AsSystem{T}"/>
///     (<c>Using(ImpersonateAsSystem, Defer(factory))</c>) so it's constructed and subscribed under the
///     system identity — mirrors <c>EventSubscriptionRunner</c>.</item>
///   <item>Writes go through <see cref="IMeshService.CreateOrUpdateNode"/> (idempotent upsert — race-free
///     re-runs) and <see cref="IMeshService.DeleteNode"/>. The child nodes match the platform shape
///     <see cref="ModelProviderService"/> emits (<c>ProviderRef</c> → the parent provider node, no key on
///     the child), so the chat-client factory resolves credentials the same way.</item>
/// </list>
/// </para>
///
/// <para>The reconcile diffs the endpoint's live list against a live catalog snapshot: additions are the
/// point of the feature; removals reflect an <c>ollama rm</c>. Two guards keep it safe: an empty/failed
/// fetch is skipped entirely (never wipes the catalog on a transient blip), and the embedding model
/// (<c>Embedding:Model</c>, e.g. <c>bge-m3</c>) is excluded so it never pollutes the chat picker.</para>
/// </summary>
public sealed class OpenAICompatibleModelSync : IHostedService, IDisposable
{
    private const string ProviderName = "OpenAICompatible";
    // "Provider/OpenAICompatible" — the parent ModelProvider node the built-in catalog seeds.
    private static readonly string ProviderPath = $"{ModelProviderNodeType.RootNamespace}/{ProviderName}";

    private readonly IMessageHub hub;
    private readonly IConfiguration configuration;
    private readonly ProviderModelLister lister;
    // 🚨 LAZY — resolved on FIRST USE, never in the constructor. Resolving IMeshService / touching the
    // workspace eagerly builds the process-wide MeshNodeStreamCache hub; doing that during host startup
    // (a hosted-service ctor runs at DI build time) can construct the cache BEFORE the Orleans stream
    // provider is ready → an NRE that kills the cache hub and wedges the whole silo. A hosted service's
    // ctor must be cheap and side-effect-free; all cache access is deferred to BeginDiscovery (below).
    private readonly Lazy<AccessService> accessService;
    private readonly Lazy<IMeshService> meshService;
    private readonly ILogger<OpenAICompatibleModelSync>? logger;

    // One-shot: the FIRST reconcile after start re-stamps EVERY discovered model (probe + upsert), so
    // nodes created before ModelDefinition.SupportsTools existed get their capability backfilled. After
    // that the normal presence-diff runs (no per-cycle rewrites).
    private bool backfilledToolSupport;

    // Live snapshot of the current OpenAICompatible model-child ids (kept fresh by the catalog query).
    // Instance field (never static), StringComparer.OrdinalIgnoreCase — model ids are case-insensitive.
    private volatile ImmutableHashSet<string> catalogIds =
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    private IDisposable? startupSub;
    private IDisposable? catalogSub;
    private IDisposable? timerSub;
    private IDisposable? backfillSub;

    public OpenAICompatibleModelSync(
        IMessageHub hub,
        IConfiguration configuration,
        ProviderModelLister lister,
        ILogger<OpenAICompatibleModelSync>? logger = null)
    {
        this.hub = hub;
        this.configuration = configuration;
        this.lister = lister;
        this.logger = logger;
        // Cheap, side-effect-free ctor: NO service resolution, NO workspace/cache access. IMeshService is
        // scoped per hub — resolve lazily from the mesh hub's provider on first use, never here.
        accessService = new Lazy<AccessService>(() => hub.ServiceProvider.GetRequiredService<AccessService>());
        meshService = new Lazy<IMeshService>(() => hub.ServiceProvider.GetRequiredService<IMeshService>());
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var endpoint = configuration["OpenAICompatible:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
            return Task.CompletedTask; // no endpoint → nothing to probe or discover

        // Full add/remove discovery is opt-in (DiscoverModels=true). But even with discovery OFF we run
        // a one-shot tool-capability BACKFILL for the statically-configured models: the boot seeder
        // (BuiltInLanguageModelProvider) can't probe /api/show synchronously, so it stamps the model
        // nodes with SupportsTools=null (assume supported) → the round would send tools to a tool-less
        // local model (Mythalion → HTTP 400). The backfill probes each configured model and stamps its
        // KNOWN capability. So an endpoint alone is enough to activate this service.
        var discover = GetBool(configuration, "OpenAICompatible:DiscoverModels", false);
        var apiKey = configuration["OpenAICompatible:ApiKey"] ?? string.Empty;
        var embeddingModel = configuration["Embedding:Model"];
        // The statically-configured model ids (OpenAICompatible:Models[]) — what the boot seeder laid
        // down. Empty when discovery owns the child set (discover=true).
        var configuredModels = configuration.GetSection("OpenAICompatible:Models").Get<string[]>()
            ?? Array.Empty<string>();
        var initialDelay = TimeSpan.FromSeconds(
            Math.Max(0, GetInt(configuration, "OpenAICompatible:DiscoverInitialDelaySeconds", 20)));
        var period = TimeSpan.FromSeconds(
            Math.Max(15, GetInt(configuration, "OpenAICompatible:DiscoverIntervalSeconds", 120)));

        // 🚨 Defer EVERY mesh-cache touch (the catalog query AND the reconcile/backfill) until initialDelay
        // past startup, off any hub thread. StartAsync runs during host startup, when the Orleans stream
        // provider may not be ready; touching the workspace/cache then constructs the process cache hub
        // too early and NREs. The one-shot timer here does nothing but schedule BeginDiscovery later, so
        // this hosted service is inert during the fragile startup window. Wrapped so it can never wedge.
        startupSub = Observable.Timer(initialDelay, TaskPoolScheduler.Default)
            .Subscribe(_ =>
            {
                try
                {
                    BeginDiscovery(endpoint!, apiKey, embeddingModel, period, discover, configuredModels);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "[OpenAICompatibleModelSync] failed to start — disabled for this run");
                }
            });

        if (discover)
            logger?.LogInformation(
                "[OpenAICompatibleModelSync] Ollama/OpenAI-compatible model discovery enabled (endpoint {Endpoint}, first run in {Delay}s, every {Period}s)",
                endpoint, initialDelay.TotalSeconds, period.TotalSeconds);
        else
            logger?.LogInformation(
                "[OpenAICompatibleModelSync] Ollama/OpenAI-compatible tool-capability backfill enabled (endpoint {Endpoint}, runs once in {Delay}s; discovery off)",
                endpoint, initialDelay.TotalSeconds);
        return Task.CompletedTask;
    }

    // Runs once, initialDelay past startup (Orleans is up by now), off any hub thread. Sets up the live
    // catalog snapshot AND the periodic reconcile. This is the FIRST point the mesh cache is touched.
    private void BeginDiscovery(
        string endpoint, string apiKey, string? embeddingModel, TimeSpan period,
        bool discover, IReadOnlyList<string> configuredModels)
    {
        if (!discover)
        {
            // Discovery OFF: statically-configured models. Run a one-shot tool-capability backfill only
            // — NO live catalog snapshot, NO add/remove. Probe each configured model, stamp SupportsTools.
            BackfillToolSupport(endpoint, configuredModels, embeddingModel);
            return;
        }

        // (a) Live snapshot of the current OpenAICompatible LanguageModel child ids (system read — the
        //     Provider partition needs an identity). Constant query id: one registry entry, no leak.
        catalogSub = AsSystem(() => hub.GetWorkspace()
                .GetQuery("openaicompatible-model-catalog",
                    $"namespace:{ProviderPath} nodeType:{LanguageModelNodeType.NodeType} scope:descendants")
                .Select(nodes => nodes
                    .Select(n => n.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)))
            .Subscribe(
                ids => catalogIds = ids,
                ex => logger?.LogWarning(ex, "[OpenAICompatibleModelSync] catalog query failed"));

        // (b) Periodic reconcile: fetch the endpoint's live model list, diff against the catalog,
        //     create/delete. Fires immediately (we're already past initialDelay) then every period. A
        //     failed cycle is logged and the loop continues (no watchdog resubscribe — the timer re-fires).
        timerSub = Observable.Timer(TimeSpan.Zero, period, TaskPoolScheduler.Default)
            // Concat (not SelectMany): reconcile cycles run STRICTLY ONE AT A TIME — a tick that fires
            // while the previous cycle is still running queues behind it instead of overlapping. This
            // keeps the single-shot backfill flag and the catalog diff free of any concurrent access.
            .Select(_ => Observable.Defer(() => Reconcile(endpoint, apiKey, embeddingModel))
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[OpenAICompatibleModelSync] discovery cycle failed (endpoint {Endpoint}) — will retry next cycle",
                        endpoint);
                    return Observable.Return(Unit.Default);
                }))
            .Concat()
            .Subscribe(_ => { },
                ex => logger?.LogError(ex, "[OpenAICompatibleModelSync] discovery loop terminated"));
    }

    // One-shot capability backfill for STATICALLY-configured OpenAICompatible models (discovery off).
    // The boot seeder can't probe /api/show, so it stamps SupportsTools=null (assume supported). Here,
    // well past startup, probe each configured chat model and stamp its KNOWN tool capability via a
    // NARROW stream.Update (only SupportsTools — never clobbers the seed's pricing/icon/order). No
    // add/remove: that is discovery's job (discover=true), not this.
    private void BackfillToolSupport(string endpoint, IReadOnlyList<string> configuredModels, string? embeddingModel)
    {
        var chatModels = configuredModels
            .Where(id => !string.IsNullOrWhiteSpace(id) && !IsEmbedding(id, embeddingModel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (chatModels.Length == 0)
            return;

        backfillSub = chatModels.ToObservable()
            .Select(modelId => Observable.Defer(() => BackfillOne(endpoint, modelId))
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[OpenAICompatibleModelSync] tool-capability backfill for {Id} failed", modelId);
                    return Observable.Return(Unit.Default);
                }))
            .Concat() // one at a time — small list, no write storm
            .Subscribe(_ => { },
                ex => logger?.LogWarning(ex, "[OpenAICompatibleModelSync] tool-capability backfill terminated"));
    }

    // Probe ONE statically-seeded model and stamp SupportsTools only if it actually changes.
    private IObservable<Unit> BackfillOne(string endpoint, string modelId)
    {
        var path = $"{ProviderPath}/{modelId}";
        // Authoritative live read of the seeded node (waits for the boot seed to land; NOT the lagged
        // synced query). System read — the Provider partition needs an identity.
        return AsSystem(() => hub.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n?.Content is ModelDefinition)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30)))
            .SelectMany(node => lister.SupportsTools(endpoint, modelId)
                .Catch<bool?, Exception>(_ => Observable.Return<bool?>(null))
                .SelectMany(probed =>
                {
                    var def = (ModelDefinition)node.Content!;
                    if (!ShouldBackfill(def.SupportsTools, probed))
                        return Observable.Return(Unit.Default); // indeterminate or already correct → no write
                    logger?.LogInformation(
                        "[OpenAICompatibleModelSync] backfilling SupportsTools={Val} for {Id}", probed, modelId);
                    return AsSystem(() => hub.GetWorkspace().GetMeshNodeStream(path)
                            .Update(cur => cur.Content is ModelDefinition d
                                ? cur with { Content = d with { SupportsTools = probed } }
                                : cur))
                        .Select(_ => Unit.Default);
                }));
    }

    // Fetch → decide (pure ComputeDelta: drop embeddings, diff, empty-guard) → apply. Exposed internally
    // so a test can drive one reconcile deterministically.
    internal IObservable<Unit> Reconcile(string endpoint, string apiKey, string? embeddingModel) =>
        lister.ListModels(endpoint, apiKey, ProviderName, allowKeyless: true)
            .SelectMany(all =>
            {
                var delta = ComputeDelta(all, catalogIds, embeddingModel);
                if (delta.Skip)
                {
                    // An empty result (endpoint up but no models, or a malformed response) must NOT be read
                    // as "delete everything". Skip the whole reconcile — adds resume once models return.
                    logger?.LogWarning(
                        "[OpenAICompatibleModelSync] endpoint {Endpoint} returned no chat models — skipping reconcile this cycle",
                        endpoint);
                    return Observable.Return(Unit.Default);
                }
                var toAdd = delta.ToAdd;
                var toRemove = delta.ToRemove;
                if (!backfilledToolSupport)
                {
                    // First reconcile after start: re-stamp EVERY desired chat model (== the diff against
                    // an empty catalog) so nodes created before ModelDefinition.SupportsTools existed get
                    // it backfilled via the idempotent CreateOrUpdate. After this, the presence-diff runs
                    // (toAdd = genuinely new only), so there are no per-cycle rewrites.
                    backfilledToolSupport = true;
                    toAdd = ComputeDelta(all, Array.Empty<string>(), embeddingModel).ToAdd;
                }
                if (toAdd.Count == 0 && toRemove.Count == 0)
                    return Observable.Return(Unit.Default);

                logger?.LogInformation(
                    "[OpenAICompatibleModelSync] reconcile: +{Add} -{Remove} at {Endpoint}",
                    toAdd.Count, toRemove.Count, endpoint);

                // Optimistically fold the delta in so a fast next tick doesn't re-apply before the live
                // catalog query catches up; the query re-emits the ACTUAL state and self-corrects.
                catalogIds = catalogIds.Except(toRemove).Union(toAdd);

                var ops = toAdd
                    // Probe each NEW model's tool-calling capability (Ollama /api/show) BEFORE creating
                    // its node, so the agent round never sends tools to a model that can't handle them
                    // (a roleplay model would 400). Indeterminate/non-Ollama → null (assume supported).
                    .Select(id => lister.SupportsTools(endpoint, id)
                        .Catch<bool?, Exception>(_ => Observable.Return<bool?>(null))
                        .SelectMany(supportsTools =>
                            AsSystem(() => meshService.Value.CreateOrUpdateNode(BuildModelNode(id, supportsTools)))
                                .Select(_ => Unit.Default))
                        .Catch<Unit, Exception>(ex =>
                        {
                            logger?.LogWarning(ex, "[OpenAICompatibleModelSync] add model {Id} failed", id);
                            return Observable.Return(Unit.Default);
                        }))
                    .Concat(toRemove
                        .Select(id => AsSystem(() => meshService.Value.DeleteNode($"{ProviderPath}/{id}"))
                            .Select(_ => Unit.Default)
                            .Catch<Unit, Exception>(ex =>
                            {
                                logger?.LogWarning(ex, "[OpenAICompatibleModelSync] remove model {Id} failed", id);
                                return Observable.Return(Unit.Default);
                            })))
                    .ToArray();

                // Concat (not Merge): apply the create/delete writes SEQUENTIALLY, so a large discovered
                // list never fans out into a concurrent write storm against the mesh (Ollama lists are
                // small, but a generic OpenAI-compatible endpoint could return hundreds).
                return Observable.Concat(ops).LastOrDefaultAsync().Select(_ => Unit.Default);
            });

    /// <summary>
    /// Pure write-decision for the tool-capability backfill: stamp <c>SupportsTools</c> only when the
    /// probe is CONCLUSIVE (non-null) AND differs from what is already on the node. An indeterminate
    /// (<c>null</c>) probe or an already-correct value writes nothing — so an unchanged reboot never
    /// churns the node (or bumps its version). Split out so it is deterministically testable.
    /// </summary>
    internal static bool ShouldBackfill(bool? current, bool? probed) => probed is not null && current != probed;

    /// <summary>The reconcile decision, split out as a pure function so it is deterministically testable.</summary>
    internal readonly record struct CatalogDelta(
        IReadOnlyList<string> ToAdd, IReadOnlyList<string> ToRemove, bool Skip);

    /// <summary>
    /// Pure diff: filter the endpoint's model list down to chat models (drop embeddings), then compute
    /// what to add / remove versus the <paramref name="currentCatalog"/>. An all-empty desired set is
    /// reported as <see cref="CatalogDelta.Skip"/> so the caller never wipes the catalog on a blip.
    /// </summary>
    internal static CatalogDelta ComputeDelta(
        IReadOnlyList<string> endpointModels,
        IReadOnlyCollection<string> currentCatalog,
        string? embeddingModel)
    {
        var desired = endpointModels.Where(id => !string.IsNullOrWhiteSpace(id) && !IsEmbedding(id, embeddingModel))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (desired.Count == 0)
            return new CatalogDelta(Array.Empty<string>(), Array.Empty<string>(), Skip: true);

        var current = currentCatalog.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredSet = desired.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = desired.Where(id => !current.Contains(id)).ToList();
        var toRemove = current.Where(id => !desiredSet.Contains(id)).ToList();
        return new CatalogDelta(toAdd, toRemove, Skip: false);
    }

    // The platform-provider LanguageModel child shape (mirrors ModelProviderService.CreateProvider):
    // no key on the child, endpoint/key resolved by following ProviderRef → the parent provider node.
    // supportsTools: the endpoint's declared tool-calling capability (null = unknown → assume supported).
    internal static MeshNode BuildModelNode(string modelId, bool? supportsTools = null)
    {
        var pricing = ModelPricing.Default(modelId); // null for local models → tokens shown without a cost
        var def = new ModelDefinition
        {
            Id = modelId,
            DisplayName = modelId,
            Provider = ProviderName,
            Endpoint = null,           // resolver follows ProviderRef
            ApiKeySecretRef = null,    // never a key on a publicly-readable model child
            ProviderRef = ProviderPath,
            Order = 5,                 // OpenAICompatible catalog order
            SupportsTools = supportsTools, // gates whether the agent round sends tool definitions
            InputPricePerMillionTokens = pricing?.InputPerMillion,
            OutputPricePerMillionTokens = pricing?.OutputPerMillion,
            Currency = pricing?.Currency
        };
        return new MeshNode(modelId, ProviderPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            Category = "Models",
            Icon = "Sparkle",
            State = MeshNodeState.Active,
            MainNode = $"{ProviderPath}/{modelId}",
            // create-if-absent: never pruned by the static importer, survives redeploys.
            SyncBehavior = SyncBehavior.ExcludeThisAndChildren,
            Content = def
        };
    }

    // Exclude the deployment's embedding model from the chat catalog. The authoritative signal is the
    // configured Embedding:Model (NOT a hardcoded model-name guess) — compared on the bare name (before
    // the ':tag') so the config value "my-embedder" matches the endpoint's "my-embedder:latest".
    internal static bool IsEmbedding(string modelId, string? embeddingModel)
    {
        if (string.IsNullOrWhiteSpace(embeddingModel))
            return false;
        return string.Equals(Bare(modelId), Bare(embeddingModel), StringComparison.OrdinalIgnoreCase);
    }

    private static string Bare(string id)
    {
        var idx = id.IndexOf(':');
        return idx < 0 ? id : id[..idx];
    }

    // 🚨 Typed config reads must treat an EMPTY/whitespace value as ABSENT, not as a present-but-unparseable
    // value. The Helm ConfigMap renders every allow-listed key even when unset, emitting an empty string
    // (`OpenAICompatible__DiscoverModels: ""`). The framework's GetValue<bool>/<int> THROWS on "" before its
    // own default applies, which kills host startup (issue #352). Reading the raw string and TryParse-ing it
    // honours the "inert unless explicitly set" contract regardless of how the deployment surface renders an
    // unset key — and protects every future non-string key from re-arming the same trap.
    internal static bool GetBool(IConfiguration configuration, string key, bool defaultValue)
    {
        var raw = configuration[key];
        return string.IsNullOrWhiteSpace(raw) ? defaultValue
            : bool.TryParse(raw, out var v) ? v : defaultValue;
    }

    internal static int GetInt(IConfiguration configuration, string key, int defaultValue)
    {
        var raw = configuration[key];
        return string.IsNullOrWhiteSpace(raw) ? defaultValue
            : int.TryParse(raw, out var v) ? v : defaultValue;
    }

    // Construct AND subscribe under the system identity (no ambient AccessContext in a hosted service).
    private IObservable<T> AsSystem<T>(Func<IObservable<T>> factory) =>
        Observable.Using(accessService.Value.ImpersonateAsSystem, _ => Observable.Defer(factory));

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        startupSub?.Dispose();
        catalogSub?.Dispose();
        timerSub?.Dispose();
        backfillSub?.Dispose();
    }
}
