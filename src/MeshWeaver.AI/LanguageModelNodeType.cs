using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI;

/// <summary>
/// Mesh-node type for AI language models. Companion to <see cref="AgentNodeType"/>.
///
/// <para>Two surfaces feed this:</para>
/// <list type="bullet">
///   <item><b>Platform models</b> — <see cref="LanguageModelCatalogOptions.Sources"/>
///         entries pair a config section (e.g. <c>Anthropic</c>) with a
///         provider label. <see cref="BuiltInLanguageModelProvider"/>
///         reads <c>{section}:Models[]</c> from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
///         at static-node-provider time and emits one
///         <c>nodeType:LanguageModel</c> MeshNode per entry under
///         <see cref="RootNamespace"/>.</item>
///   <item><b>Bring-your-own models</b> — anyone can create a node of this
///         type at any path with <see cref="ModelDefinition"/> content; the
///         chat picker discovers it via the same synced query that finds
///         agents (<c>nodeType:Agent|LanguageModel</c>).</item>
/// </list>
///
/// <para>Public-read by default — model identity and provider are not
/// secrets. Credentials live behind <see cref="ModelDefinition.ApiKeySecretRef"/>
/// in a secret store, never in the node content itself.</para>
/// </summary>
public static class LanguageModelNodeType
{
    /// <summary>NodeType discriminator value.</summary>
    public const string NodeType = "LanguageModel";

    /// <summary>Conventional namespace for model nodes (<c>Model/&lt;id&gt;</c>).</summary>
    public const string RootNamespace = "Model";

    /// <summary>
    /// Registers the built-in <c>LanguageModel</c> MeshNode definition + the
    /// <see cref="BuiltInLanguageModelProvider"/> that materialises every
    /// configured model as a static node, plus public-read access. Auto-seeds
    /// the well-known catalog sources (Anthropic, AzureFoundry, OpenAI) so a
    /// stock deploy with those factories' configs Just Works.
    /// </summary>
    public static TBuilder AddLanguageModelType<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<LanguageModelCatalogOptions>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IStaticNodeProvider, BuiltInLanguageModelProvider>());
            return services;
        });

        // Seed well-known catalog sources — each provider's factory reads
        // its config from the same section, so its `Models[]` is the
        // canonical model list. Custom providers can add more via
        // AddLanguageModelCatalogSource.
        builder.AddLanguageModelCatalogSource("Anthropic", "Azure Claude", order: 1);
        builder.AddLanguageModelCatalogSource("AzureFoundry", "Azure Foundry", order: 2);
        builder.AddLanguageModelCatalogSource("OpenAI", "OpenAI", order: 3);
        return builder;
    }

    /// <summary>
    /// Adds a catalog source: a config section to scan for <c>Models[]</c>
    /// when populating the <c>nodeType:LanguageModel</c> partition.
    ///
    /// <para>Idempotent on (sectionName, providerName) — safe to call from
    /// multiple <c>builder.ConfigureServices</c> blocks. Mutates the
    /// <see cref="LanguageModelCatalogOptions"/> singleton directly
    /// instead of using the <c>IOptions&lt;T&gt;</c> Configure pipeline,
    /// which didn't propagate to the mesh hub's DI scope (live
    /// <c>namespace:Model</c> queries returned only the access policy
    /// because Sources was empty at provider-resolve time).</para>
    /// </summary>
    public static TBuilder AddLanguageModelCatalogSource<TBuilder>(
        this TBuilder builder,
        string sectionName,
        string providerName,
        int order = 0)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<LanguageModelCatalogOptions>();

            // Get or create the singleton instance and mutate it directly.
            // The Add helper deduplicates by (section, provider).
            var existing = services.FirstOrDefault(d =>
                d.ServiceType == typeof(LanguageModelCatalogOptions) &&
                d.ImplementationInstance is LanguageModelCatalogOptions);
            LanguageModelCatalogOptions instance;
            if (existing?.ImplementationInstance is LanguageModelCatalogOptions inst)
            {
                instance = inst;
            }
            else
            {
                instance = new LanguageModelCatalogOptions();
                // Replace any factory registration with our concrete
                // instance so DI returns this exact object at resolve time.
                for (var i = services.Count - 1; i >= 0; i--)
                {
                    if (services[i].ServiceType == typeof(LanguageModelCatalogOptions))
                        services.RemoveAt(i);
                }
                services.AddSingleton(instance);
            }

            instance.Add(new LanguageModelCatalogSource(sectionName, providerName, order));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// MeshNode definition for <c>nodeType:LanguageModel</c>. Carries the
    /// per-instance hub configuration that wires
    /// <see cref="ModelDefinition"/> as the content type so reads through
    /// <see cref="MeshWeaver.Mesh.Services.IMeshService"/> /
    /// <see cref="MeshWeaver.Mesh.Services.IMeshQuery"/> deserialise into
    /// the typed record.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Language Model",
        Icon = "/static/NodeTypeIcons/sparkle.svg",
        AssemblyLocation = typeof(LanguageModelNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ModelDefinition>())
    };
}
