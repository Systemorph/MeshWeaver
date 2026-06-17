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
        // Stable de-dup: first registered source wins on Id collision —
        // matches the order callers register them via
        // AddLanguageModelCatalogSource.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = new List<MeshNode>();

        foreach (var source in options.Sources)
        {
            string[]? models;
            try
            {
                models = configuration
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

            // Read driver config (Endpoint + ApiKey) from the same section
            // the legacy IOptions<...Configuration> binding read from. Stamping
            // these on the ModelDefinition makes the model MeshNode the source
            // of truth for driver config — the factory reads them off the
            // selected model instead of an out-of-band IOptions binding, and
            // user-authored Model nodes can override the built-in defaults.
            string? endpoint = null;
            string? apiKey = null;
            try
            {
                endpoint = configuration[$"{source.SectionName}:Endpoint"];
                apiKey = configuration[$"{source.SectionName}:ApiKey"];
            }
            catch { /* malformed section — skip stamping */ }

            // Parse IConfiguration into the canonical ModelProvider mesh node:
            // every credential the system ships with becomes a node in the
            // root catalog. ModelProvider is NOT WithPublicRead (no
            // ConfigureNodeTypeAccess call) so only callers with
            // Permission.Api on the root namespace see the ApiKey; ordinary
            // user-context reads in their own partition see only their own
            // (user-authored) ModelProvider rows. Publicly-visible LanguageModel
            // siblings still carry NO key — that protection is intact.
            var hasAnySignal = (models != null && models.Length > 0)
                || !string.IsNullOrEmpty(endpoint)
                || !string.IsNullOrEmpty(apiKey);
            if (hasAnySignal)
            {
                var providerConfig = new ModelProviderConfiguration
                {
                    Provider = source.ProviderName,
                    ApiKey = apiKey,
                    Endpoint = endpoint,
                    Label = source.ProviderName,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Models = models is { Length: > 0 }
                        ? models.Where(m => !string.IsNullOrWhiteSpace(m)).ToImmutableArray()
                        : ImmutableArray<string>.Empty
                };
                emitted.Add(new MeshNode(source.ProviderName, ModelProviderNodeType.RootNamespace)
                {
                    NodeType = ModelProviderNodeType.NodeType,
                    Name = source.ProviderName,
                    Category = "Providers",
                    Icon = "Key",
                    Content = providerConfig
                });
            }

            if (models == null || models.Length == 0)
            {
                logger?.LogDebug(
                    "BuiltInLanguageModelProvider: '{Section}:Models' empty — provider {Provider} contributes nothing",
                    source.SectionName, source.ProviderName);
                continue;
            }

            // 🚦 Only surface a provider's models when it's actually CONFIGURED.
            // Api providers (RequiresApiKey) need BOTH an Endpoint and an ApiKey in
            // config; keyless/CLI providers (RequiresApiKey=false — Claude Code,
            // Copilot) need neither. An unconfigured Api provider's Models[] is just a
            // default catalog the deployment never wired up (e.g. an "Azure" section
            // listing Claude ids with no Endpoint/ApiKey) — surfacing those puts
            // selectable-but-unusable entries in the /model picker (the reported
            // "Azure Claude shows even though nothing is configured" bug). The
            // ModelProvider node above is STILL emitted so Settings → Models can render
            // its configure form; only the selectable LanguageModel catalog entries are
            // gated on having credentials.
            var isConfigured = !source.RequiresApiKey
                || (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey));
            if (!isConfigured)
            {
                logger?.LogDebug(
                    "BuiltInLanguageModelProvider: provider {Provider} not configured (Endpoint/ApiKey unset) — hiding its {Count} model(s) from the catalog until configured",
                    source.ProviderName, models.Length);
                continue;
            }

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
                    // ApiKey NEVER on a LanguageModel node — these are
                    // WithPublicRead. The factory's IOptions fallback supplies
                    // the system-default key for static catalog entries.
                    ApiKeySecretRef = null,
                    // Reference the static ModelProvider node emitted above.
                    // Resolver follows this pointer; user-partition ModelProvider
                    // nodes override via their own ProviderRef on child
                    // LanguageModel nodes.
                    ProviderRef = hasAnySignal
                        ? $"{ModelProviderNodeType.RootNamespace}/{source.ProviderName}"
                        : null,
                    Order = source.Order
                };

                // Static LanguageModel nodes live UNDER their provider's
                // satellite path: _Provider/{providerName}/{modelId}. Matches
                // the user-partition layout ({userPath}/_Provider/{providerName}/{modelId})
                // so the picker can use ONE namespace per query path —
                // the documented shape for synced-collection multi-query
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

        // Only seed the read-only access policy if we actually have models —
        // an empty namespace with just a policy node is "crap" (user's
        // word) that pollutes namespace:Model queries with nothing useful.
        if (emitted.Count > 0)
        {
            // 🚨 Policy MUST be on ModelProviderNodeType.RootNamespace ("_Provider") — the partition the
            // models actually live in (the modelNamespace above) AND the one the /model picker queries
            // (BuiltInCommandProvider: "namespace:_Provider nodeType:LanguageModel scope:descendants").
            // It was previously seeded on LanguageModelNodeType.RootNamespace ("Model") — a DIFFERENT,
            // model-less partition — so the "_Provider" catalog had NO access policy at all and was
            // unreadable under a real user's identity.
            yield return new MeshNode("_Policy", ModelProviderNodeType.RootNamespace)
            {
                NodeType = "PartitionAccessPolicy",
                Name = "Access Policy",
                Content = new PartitionAccessPolicy
                {
                    // 🚨 World-readable, exactly like the Agent + Harness catalogs
                    // (BuiltInAgentProvider / BuiltInHarnessProvider both set PublicRead=true).
                    // The /model picker queries `namespace:_Provider nodeType:LanguageModel` UNDER
                    // the user's identity; without PublicRead the partition isn't readable, so RLS
                    // filters out every model → empty picker even though the catalog is synced + Active.
                    // This grant was MISSING here (the Agent catalog had it, which is why agents showed
                    // and models didn't). Read-only: the model nodes carry NO ApiKey (it's gated
                    // separately on ModelProvider via Permission.Api), so public READ of the catalog is safe.
                    PublicRead = true,
                    Create = false,
                    Update = false,
                    Delete = false,
                    Comment = false,
                    Thread = false
                }
            };
        }

        logger?.LogDebug(
            "BuiltInLanguageModelProvider: emitted {Count} Model nodes from {Sources} catalog source(s)",
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
