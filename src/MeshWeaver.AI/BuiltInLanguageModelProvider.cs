using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Surfaces every platform-shipped model as a static
/// <c>nodeType:LanguageModel</c> MeshNode under the
/// <see cref="LanguageModelNodeType.RootNamespace"/>.
///
/// <para>Reads <see cref="LanguageModelCatalogOptions.Sources"/> — a plain
/// singleton populated by
/// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}(TBuilder, LanguageModelCatalogSource)"/>.
/// Each entry pairs a config section (where the deployed
/// <see cref="IChatClientFactory"/> reads its config from) with the
/// provider label that should appear on the resulting Model node. The
/// section's <c>Models[]</c> array becomes the model id list.</para>
///
/// <para>🚨 Catalog options are a plain singleton, NOT
/// <see cref="Microsoft.Extensions.Options.IOptions{T}"/>: the
/// <c>Configure&lt;T&gt;</c> pipeline didn't survive to the mesh hub's DI
/// scope (live <c>namespace:Model</c> queries returned only the access
/// policy because <c>Sources</c> was empty at provider-resolve time).
/// Direct singleton + helper that idempotently appends sidesteps the
/// scope mismatch.</para>
///
/// <para>Net effect: a query like <c>namespace:Model nodeType:LanguageModel</c>
/// always returns the deployed catalog without depending on
/// <see cref="IChatClientFactory"/> registrations (which live on the web
/// host, not the mesh hub).</para>
/// </summary>
public class BuiltInLanguageModelProvider : IStaticNodeProvider
{
    private readonly IConfiguration configuration;
    private readonly LanguageModelCatalogOptions options;
    private readonly ILogger<BuiltInLanguageModelProvider>? logger;

    public BuiltInLanguageModelProvider(
        IConfiguration configuration,
        LanguageModelCatalogOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        this.configuration = configuration;
        this.options = options;
        this.logger = loggerFactory?.CreateLogger<BuiltInLanguageModelProvider>();
    }

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Stable de-dup: first registered source wins on model-Id collision —
        // matches the order callers register them via
        // AddLanguageModelCatalogSource.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = new List<MeshNode>();

        foreach (var source in options.Sources)
        {
            string[]? configuredModels;
            try
            {
                configuredModels = configuration
                    .GetSection($"{source.SectionName}:Models")
                    .Get<string[]>();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "BuiltInLanguageModelProvider: failed to read config section '{Section}:Models' — skipping",
                    source.SectionName);
                continue;
            }

            // Driver config (Endpoint + ApiKey) from the same section the legacy
            // IOptions<...Configuration> binding read from. The endpoint falls back to
            // the source's bootstrap DefaultEndpoint when config is empty; the ApiKey is
            // NEVER seeded from code — it stays null until an admin sets it as mesh data.
            string? endpoint = null;
            string? apiKey = null;
            try
            {
                endpoint = configuration[$"{source.SectionName}:Endpoint"];
                apiKey = configuration[$"{source.SectionName}:ApiKey"];
            }
            catch { /* malformed section — skip stamping */ }
            endpoint ??= source.DefaultEndpoint;

            // Model id list: config wins when present, else the source's bootstrap
            // defaults. Empty for catalogs that are auto-listed/added later
            // (OpenRouter, OpenAICompatible) — those ship a provider node with no children.
            var models = (configuredModels is { Length: > 0 }
                    ? configuredModels.Where(m => !string.IsNullOrWhiteSpace(m))
                    : source.EffectiveModelIds.Where(m => !string.IsNullOrWhiteSpace(m)))
                .ToImmutableArray();

            // Always emit ONE ModelProvider node per source at Provider/{ProviderName},
            // marked ExcludeThisAndChildren so the static importer CREATES it on first boot and
            // NEVER overwrites it again — admin edits to endpoint/key/models survive redeploys
            // (create-if-absent). This node is the source of truth for driver config: the factory
            // reads endpoint/key off the selected model's resolved provider node. ModelProvider
            // is NOT WithPublicRead, so only callers with Permission.Api see the ApiKey; the
            // _Policy below opens public READ of the (key-less) LanguageModel children.
            var providerConfig = new ModelProviderConfiguration
            {
                Provider = source.ProviderName,
                ApiKey = apiKey,
                Endpoint = endpoint,
                Label = source.ProviderName,
                CreatedAt = DateTimeOffset.UtcNow,
                Models = models
            };
            emitted.Add(new MeshNode(source.ProviderName, ModelProviderNodeType.RootNamespace)
            {
                NodeType = ModelProviderNodeType.NodeType,
                Name = source.ProviderName,
                Category = "Providers",
                Icon = "Key",
                // create-if-absent: importer seeds it once, then admin owns it.
                SyncBehavior = SyncBehavior.ExcludeThisAndChildren,
                Content = providerConfig
            });

            // Emit a public, key-less LanguageModel child per model id at
            // Provider/{ProviderName}/{modelId}. No credential gate — the child carries
            // NO ApiKey (it's read-only/public) and the ExcludeThisAndChildren parent protects
            // the whole subtree from overwrite AND prune. Children keep default SyncBehavior.
            foreach (var modelId in models)
            {
                if (string.IsNullOrWhiteSpace(modelId)) continue;
                if (!seen.Add(modelId)) continue;

                var def = new ModelDefinition
                {
                    Id = modelId,
                    DisplayName = modelId,
                    Provider = source.ProviderName,
                    Endpoint = endpoint,
                    // ApiKey NEVER on a LanguageModel node — these are publicly readable.
                    // The factory resolves the key from the parent ModelProvider node.
                    ApiKeySecretRef = null,
                    // Reference the static ModelProvider node emitted above. Resolver follows
                    // this pointer; context-partition ModelProvider nodes override via their
                    // own ProviderRef on child LanguageModel nodes.
                    ProviderRef = $"{ModelProviderNodeType.RootNamespace}/{source.ProviderName}",
                    Order = source.Order,
                    // Seed the published default price (USD per 1M tokens) for known
                    // model ids so the token-cost summaries show a real cost out of
                    // the box; users can override per model in the Models settings.
                    InputPricePerMillionTokens = ModelPricing.Default(modelId)?.InputPerMillion,
                    OutputPricePerMillionTokens = ModelPricing.Default(modelId)?.OutputPerMillion,
                    Currency = ModelPricing.Default(modelId)?.Currency
                };

                // Static LanguageModel nodes live UNDER their provider's satellite path:
                // Provider/{providerName}/{modelId}. Matches the context-partition layout
                // ({contextPath}/Provider/{providerName}/{modelId}) so the picker can use ONE
                // namespace per query path — the documented shape for synced-collection multi-query
                // (varying scope/path, same nodeType filter).
                var modelNamespace = $"{ModelProviderNodeType.RootNamespace}/{source.ProviderName}";
                emitted.Add(new MeshNode(modelId, modelNamespace)
                {
                    NodeType = LanguageModelNodeType.NodeType,
                    Name = modelId,
                    Category = "Models",
                    Icon = "Sparkle",
                    Content = def
                });
            }
        }

        // ALWAYS seed the read-only access policy for the catalog partition.
        yield return new MeshNode("_Policy", ModelProviderNodeType.RootNamespace)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                // 🚨 PublicRead grants every user READ of the catalog (the /model picker queries
                // `namespace:Provider nodeType:LanguageModel` UNDER the user's identity;
                // without PublicRead RLS filters out every model → empty picker). Read-only is
                // safe: the LanguageModel children carry NO ApiKey (gated separately on
                // ModelProvider via Permission.Api).
                //
                // Create/Update/Delete = true LIFT the partition cap so platform admins can WRITE
                // provider nodes here (managing the shared catalog — endpoints, keys, enabled
                // models). These flags are pure CEILINGS — they do NOT grant non-admins write: a
                // non-admin holds no write role anywhere in the Provider scope hierarchy, so they
                // stay read-only. Platform admins get standing write via the Provider/_Access Admin
                // grant seeded by GlobalAdminSeed (the Provider catalog is top-level, so the Admin-
                // partition grant no longer covers it the way Admin/Provider once did).
                PublicRead = true,
                Create = true,
                Update = true,
                Delete = true,
                Comment = false,
                Thread = false
            }
        };

        // ALSO seed the read-only policy for the LanguageModel partition
        // (LanguageModelNodeType.RootNamespace, lowercased to the `model` schema). Chat /
        // the model picker / model resolution read this partition UNDER THE USER'S IDENTITY
        // (e.g. GetDataRequest hub=model). Without a PublicRead policy a non-admin is denied
        // "lacks Read permission on 'model'", which comes back as a DeliveryFailureException
        // and crashes the chat round (atioz 2026-06-23, rsalzmann/Robert). The catalog source
        // emitted only the Provider policy after the refactor; this restores the Model one.
        // Read-only + same lifted write CEILINGS as Provider (admins manage; non-admins hold
        // no write role here so they stay read-only).
        if (!string.Equals(LanguageModelNodeType.RootNamespace, ModelProviderNodeType.RootNamespace, StringComparison.Ordinal))
            yield return new MeshNode("_Policy", LanguageModelNodeType.RootNamespace)
            {
                NodeType = "PartitionAccessPolicy",
                Name = "Access Policy",
                Content = new PartitionAccessPolicy
                {
                    PublicRead = true,
                    Create = true,
                    Update = true,
                    Delete = true,
                    Comment = false,
                    Thread = false
                }
            };

        logger?.LogDebug(
            "BuiltInLanguageModelProvider: emitted {Count} model-catalog nodes from {Sources} catalog source(s)",
            emitted.Count, options.Sources.Count);

        foreach (var node in emitted)
            yield return node;
    }
}

/// <summary>
/// Catalog of <see cref="IConfiguration"/> sections that
/// <see cref="BuiltInLanguageModelProvider"/> scans for models. Plain
/// singleton — populated by
/// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}(TBuilder, LanguageModelCatalogSource)"/>.
/// </summary>
public class LanguageModelCatalogOptions
{
    /// <summary>Registered catalog sources, populated at mesh init time.</summary>
    public List<LanguageModelCatalogSource> Sources { get; } = new();

    /// <summary>
    /// Idempotently appends a source — does nothing if (sectionName, providerName)
    /// is already present. Called from
    /// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}(TBuilder, LanguageModelCatalogSource)"/>;
    /// safe to call multiple times across multiple
    /// <c>builder.ConfigureServices</c> blocks.
    /// </summary>
    public void Add(LanguageModelCatalogSource source)
    {
        if (Sources.Any(s =>
            s.SectionName == source.SectionName &&
            s.ProviderName == source.ProviderName))
            return;
        Sources.Add(source);
    }
}

/// <summary>
/// One catalog source: a config section to scan for <c>Models[]</c>, the
/// provider label to stamp on each resulting Model node, and the
/// provider's bootstrap profile — default endpoint, default model ids
/// (used when a user pastes a key in the Models settings tab and
/// <c>ModelProviderService</c> auto-creates the LanguageModel children),
/// and whether the provider requires an API key at all (false for keyless
/// providers like GitHub Copilot or the local Claude Code CLI).
///
/// <para>Each provider package registers its own source via
/// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}(TBuilder, LanguageModelCatalogSource)"/>
/// — there is NO central registry. Consumers (Models settings tab,
/// <c>ModelProviderService.CreateProvider</c>) enumerate the live
/// <see cref="LanguageModelCatalogOptions.Sources"/>.</para>
/// </summary>
public record LanguageModelCatalogSource(
    string SectionName,
    string ProviderName,
    int Order = 0,
    string? DisplayLabel = null,
    string? DefaultEndpoint = null,
    ImmutableArray<string> DefaultModelIds = default,
    bool RequiresApiKey = true,
    ProviderKind Kind = ProviderKind.Api)
{
    /// <summary>Effective display label — falls back to <see cref="ProviderName"/> when not supplied.</summary>
    public string EffectiveLabel => DisplayLabel ?? ProviderName;

    /// <summary>Defensive default for <see cref="DefaultModelIds"/> — record syntax can leave it uninitialised.</summary>
    public ImmutableArray<string> EffectiveModelIds =>
        DefaultModelIds.IsDefault ? ImmutableArray<string>.Empty : DefaultModelIds;
}

/// <summary>
/// How a provider authenticates and what the Settings → Models card renders for it.
///
/// <list type="bullet">
///   <item><see cref="Api"/> — bring-your-own-key (Azure AI Foundry, Azure OpenAI,
///         Anthropic, OpenAI). The card shows an endpoint/key form plus a fetched,
///         checkable list of models to enable.</item>
///   <item><see cref="Cli"/> — co-hosted, subscription-based CLI (Claude Code,
///         GitHub Copilot). No model list; the card shows a login status dot plus a
///         Connect button that delegates to the CLI's native login (paste-code for
///         Claude, device-flow for Copilot).</item>
/// </list>
/// </summary>
public enum ProviderKind
{
    /// <summary>Bring-your-own-key provider — endpoint/key form + model list.</summary>
    Api,

    /// <summary>Co-hosted CLI provider — login status + Connect, no model list.</summary>
    Cli,
}
