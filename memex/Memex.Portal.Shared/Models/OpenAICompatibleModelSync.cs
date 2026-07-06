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
    private readonly AccessService accessService;
    private readonly IMeshService meshService;
    private readonly ILogger<OpenAICompatibleModelSync>? logger;

    // Live snapshot of the current OpenAICompatible model-child ids (kept fresh by the catalog query).
    // Instance field (never static), StringComparer.OrdinalIgnoreCase — model ids are case-insensitive.
    private volatile ImmutableHashSet<string> catalogIds =
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    private IDisposable? catalogSub;
    private IDisposable? timerSub;

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
        // IMeshService is scoped per hub — resolve from the mesh hub's provider, not the web root.
        accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var discover = configuration.GetValue("OpenAICompatible:DiscoverModels", false);
        var endpoint = configuration["OpenAICompatible:Endpoint"];
        if (!discover || string.IsNullOrWhiteSpace(endpoint))
            return Task.CompletedTask; // inert unless explicitly enabled AND an endpoint is set

        var apiKey = configuration["OpenAICompatible:ApiKey"] ?? string.Empty;
        var embeddingModel = configuration["Embedding:Model"];
        var initialDelay = TimeSpan.FromSeconds(
            Math.Max(0, configuration.GetValue("OpenAICompatible:DiscoverInitialDelaySeconds", 20)));
        var period = TimeSpan.FromSeconds(
            Math.Max(15, configuration.GetValue("OpenAICompatible:DiscoverIntervalSeconds", 120)));

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
        //     create/delete. Runs at initialDelay then every period, off any hub thread. A failed cycle
        //     is logged and the loop continues (no watchdog resubscribe — the timer already re-fires).
        timerSub = Observable.Timer(initialDelay, period, TaskPoolScheduler.Default)
            .SelectMany(_ => Reconcile(endpoint!, apiKey, embeddingModel)
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[OpenAICompatibleModelSync] discovery cycle failed (endpoint {Endpoint}) — will retry next cycle",
                        endpoint);
                    return Observable.Return(Unit.Default);
                }))
            .Subscribe(_ => { },
                ex => logger?.LogError(ex, "[OpenAICompatibleModelSync] discovery loop terminated"));

        logger?.LogInformation(
            "[OpenAICompatibleModelSync] Ollama/OpenAI-compatible model discovery enabled (endpoint {Endpoint}, first run in {Delay}s, every {Period}s)",
            endpoint, initialDelay.TotalSeconds, period.TotalSeconds);
        return Task.CompletedTask;
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
                if (delta.ToAdd.Count == 0 && delta.ToRemove.Count == 0)
                    return Observable.Return(Unit.Default);

                logger?.LogInformation(
                    "[OpenAICompatibleModelSync] reconcile: +{Add} -{Remove} at {Endpoint}",
                    delta.ToAdd.Count, delta.ToRemove.Count, endpoint);

                // Optimistically fold the delta in so a fast next tick doesn't re-apply before the live
                // catalog query catches up; the query re-emits the ACTUAL state and self-corrects.
                catalogIds = catalogIds.Except(delta.ToRemove).Union(delta.ToAdd);

                var ops = delta.ToAdd
                    .Select(id => AsSystem(() => meshService.CreateOrUpdateNode(BuildModelNode(id)))
                        .Select(_ => Unit.Default)
                        .Catch<Unit, Exception>(ex =>
                        {
                            logger?.LogWarning(ex, "[OpenAICompatibleModelSync] add model {Id} failed", id);
                            return Observable.Return(Unit.Default);
                        }))
                    .Concat(delta.ToRemove
                        .Select(id => AsSystem(() => meshService.DeleteNode($"{ProviderPath}/{id}"))
                            .Select(_ => Unit.Default)
                            .Catch<Unit, Exception>(ex =>
                            {
                                logger?.LogWarning(ex, "[OpenAICompatibleModelSync] remove model {Id} failed", id);
                                return Observable.Return(Unit.Default);
                            })))
                    .ToArray();

                return Observable.Merge(ops).LastOrDefaultAsync().Select(_ => Unit.Default);
            });

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
    internal static MeshNode BuildModelNode(string modelId)
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

    // Construct AND subscribe under the system identity (no ambient AccessContext in a hosted service).
    private IObservable<T> AsSystem<T>(Func<IObservable<T>> factory) =>
        Observable.Using(accessService.ImpersonateAsSystem, _ => Observable.Defer(factory));

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        catalogSub?.Dispose();
        timerSub?.Dispose();
    }
}
