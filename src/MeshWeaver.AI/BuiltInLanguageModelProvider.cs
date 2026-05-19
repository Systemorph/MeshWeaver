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
/// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}"/>.
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

            if (models == null || models.Length == 0)
            {
                logger?.LogDebug(
                    "BuiltInLanguageModelProvider: '{Section}:Models' empty — provider {Provider} contributes nothing",
                    source.SectionName, source.ProviderName);
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
                    ApiKeySecretRef = apiKey,
                    Order = source.Order
                };

                emitted.Add(new MeshNode(modelId, LanguageModelNodeType.RootNamespace)
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
            yield return new MeshNode("_Policy", LanguageModelNodeType.RootNamespace)
            {
                NodeType = "PartitionAccessPolicy",
                Name = "Access Policy",
                Content = new PartitionAccessPolicy
                {
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
/// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}"/>.
/// </summary>
public class LanguageModelCatalogOptions
{
    /// <summary>Registered catalog sources, populated at mesh init time.</summary>
    public List<LanguageModelCatalogSource> Sources { get; } = new();

    /// <summary>
    /// Idempotently appends a source — does nothing if (sectionName, providerName)
    /// is already present. Called from
    /// <see cref="LanguageModelNodeType.AddLanguageModelCatalogSource{TBuilder}"/>;
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
/// One catalog source: a config section to scan for <c>Models[]</c> and
/// the provider label to stamp on each resulting Model node.
/// </summary>
public record LanguageModelCatalogSource(string SectionName, string ProviderName, int Order = 0);
