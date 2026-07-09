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
///   <item><b>Platform models</b> â€” <see cref="LanguageModelCatalogOptions.Sources"/>
///         entries pair a config section (e.g. <c>Anthropic</c>) with a
///         provider label. <see cref="BuiltInLanguageModelProvider"/>
///         reads <c>{section}:Models[]</c> from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
///         at static-node-provider time and emits one
///         <c>nodeType:LanguageModel</c> MeshNode per entry under
///         <see cref="RootNamespace"/>.</item>
///   <item><b>Bring-your-own models</b> â€” anyone can create a node of this
///         type at any path with <see cref="ModelDefinition"/> content; the
///         chat picker discovers it via the same synced query that finds
///         agents (<c>nodeType:Agent|LanguageModel</c>).</item>
/// </list>
///
/// <para>Public-read by default â€” model identity and provider are not
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
    public static TBuilder AddLanguageModelType<TBuilder>(this TBuilder builder,
        IReadOnlySet<string>? serveFromPartition = null)
        where TBuilder : MeshBuilder
    {
        // DB-synced when the portal serves the model-catalog partition from the DB. The catalog now
        // lives under the "Provider" partition (ModelProviderNodeType.RootNamespace); the legacy
        // "Model" partition name is still honoured for backwards-compatible configs. On the synced
        // path the read-only in-memory static provider is skipped (Postgres serves it) AND the
        // in-memory LanguageModel type-def is registered DEFINITION-ONLY so the per-node-hub
        // persistence sampler never auto-persists it to a phantom "languagemodel" schema (42P01).
        // Mirrors HarnessNodeType / AddModelProviderType. See Doc/Architecture/NodeTypeCatalogs.md.
        var dbSynced = serveFromPartition is not null
            && (serveFromPartition.Contains("Model")
                || serveFromPartition.Contains(ModelProviderNodeType.RootNamespace));

        var typeDefinition = CreateMeshNode();
        if (dbSynced)
            typeDefinition = typeDefinition with { IsDefinitionOnly = true };
        builder.AddMeshNodes(typeDefinition);
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        // Companion NodeType: ModelProvider holds the credentials shared by
        // all child LanguageModel nodes. Registered together so a deployment
        // calling AddLanguageModelType wires the entire data shape (the
        // ChatClientCredentialResolver depends on both being available).
        builder.AddModelProviderType(serveFromPartition);
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<LanguageModelCatalogOptions>();
            services.TryAddSingleton<BuiltInLanguageModelProvider>();
            // Encryption-at-rest for ModelProvider.ApiKey. Default master key
            // comes from config (Ai:KeyProtection:MasterKey); swap in a
            // KMS/Key Vault IMasterKeyProvider for hardened deployments. With
            // no key configured both are pure passthrough (plaintext), so this
            // is safe to register unconditionally.
            services.TryAddSingleton<IMasterKeyProvider, ConfigMasterKeyProvider>();
            services.TryAddSingleton<IProviderKeyProtector, ProviderKeyProtector>();
            services.TryAddSingleton<ChatClientCredentialResolver>();
            // Headless default chat client (for background one-shot model calls, e.g. the
            // content-indexing image describer). Resolves the lowest-Order resolvable LanguageModel
            // and its serving factory — no agent, no shared-state mutation.
            services.TryAddSingleton<DefaultChatClientProvider>();
            // ModelDiscoveryService MUST be a top-level singleton on the
            // mesh hub — never on a per-thread / exec hub where its
            // synced subscriptions could get stuck behind an in-flight
            // handler. The per-thread/per-chat code paths read this
            // service from meshHub.ServiceProvider, not from their own
            // hub's DI scope.
            services.TryAddSingleton<ModelDiscoveryService>();
            // ðŸš¨ Plain AddSingleton (not TryAddEnumerable): TryAddEnumerable
            // dedupes by impl-type AND ServiceLifetime AND ImplementationFactory
            // â€” combinations that occasionally suppress the registration in
            // ways that left BuiltInLanguageModelProvider invisible to DI
            // resolution while BuiltInAgentProvider (using plain AddSingleton)
            // worked. Match the AgentProvider pattern so both follow the
            // same path.
            // 🚨 Gate the IStaticNodeProvider (feeds FindStaticNode) on !dbSynced, same as the
            // partition provider below — leaving it registered while the model-catalog partition is
            // DB-synced made the importer's inner CreateNode see the built-in catalog/Provider
            // nodes as already-present and fail "Node already exists" (atioz 2026-06-11: imported
            // 4 / failed 2, incl. Provider/_Policy + Provider/Anthropic). The
            // BuiltInLanguageModelProvider singleton stays (the import source wraps it); the
            // LanguageModel/ModelProvider NodeType defs stay via AddMeshNodes. See AddAgentType.
            if (!dbSynced)
            {
                services.AddSingleton<IStaticNodeProvider>(sp => sp.GetRequiredService<BuiltInLanguageModelProvider>());
                // Partition routing â€” the same instance feeds the routing core's
                // "Model" partition. The partition's StaticNodeStorageAdapter is
                // its storage of record; no SeedIfAbsent fan-in required. Skipped when
                // the partition is DB-synced (PG serves it instead).
                services.AddSingleton<IPartitionStorageProvider>(sp =>
                    new StaticNodePartitionStorageProvider(
                        RootNamespace,
                        sp.GetRequiredService<BuiltInLanguageModelProvider>(),
                        description: "Built-in language model catalog (read-only)."));
            }
            return services;
        });

        // No central seeding — each provider package registers its own
        // catalog source via AddLanguageModelCatalogSource in its own
        // builder extension (decentralised). See e.g.
        // AzureFoundryExtensions.AddAzureClaudeProvider().
        return builder;
    }

    /// <summary>
    /// Adds a catalog source: a config section to scan for <c>Models[]</c>
    /// when populating the <c>nodeType:LanguageModel</c> partition.
    ///
    /// <para>Idempotent on (sectionName, providerName) â€” safe to call from
    /// multiple <c>builder.ConfigureServices</c> blocks. Mutates the
    /// <see cref="LanguageModelCatalogOptions"/> singleton directly
    /// instead of using the <c>IOptions&lt;T&gt;</c> Configure pipeline,
    /// which didn't propagate to the mesh hub's DI scope (live
    /// <c>namespace:Model</c> queries returned only the access policy
    /// because Sources was empty at provider-resolve time).</para>
    /// </summary>
    /// <inheritdoc cref="AddLanguageModelCatalogSource{TBuilder}(TBuilder, LanguageModelCatalogSource)"/>
    public static TBuilder AddLanguageModelCatalogSource<TBuilder>(
        this TBuilder builder,
        string sectionName,
        string providerName,
        int order = 0)
        where TBuilder : MeshBuilder
        => builder.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(sectionName, providerName, order));

    /// <summary>
    /// Adds a fully-described catalog source — same shape as the legacy
    /// 3-arg overload but carries the provider's bootstrap profile
    /// (display label, default endpoint, default model ids,
    /// RequiresApiKey). Decentralised: each provider package self-
    /// registers via its own builder extension (see e.g.
    /// AzureFoundryExtensions.AddAzureClaudeProvider). Idempotent on
    /// (sectionName, providerName).
    /// </summary>
    public static TBuilder AddLanguageModelCatalogSource<TBuilder>(
        this TBuilder builder,
        LanguageModelCatalogSource source)
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

            instance.Add(source);
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
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ModelDefinition>())
    };
}
