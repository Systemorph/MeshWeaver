using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    /// Pseudo-provider that owns the <b>Auto</b> router. Not a vendor: Auto belongs to the platform,
    /// not to whoever ends up serving the round.
    /// </summary>
    public const string RouterProviderName = "Auto";

    /// <summary>The router's model id. Never sent on the wire — Auto dispatches before any factory sees it.</summary>
    public const string RouterModelId = "auto";

    /// <summary>
    /// The router's <see cref="MeshNode.Order"/>. Below the <c>-1</c> "make this the default"
    /// convention on purpose: Auto is the DEFAULT selection for a new thread, so it must sort ahead
    /// of whichever concrete model a deployment pinned at <c>-1</c>.
    /// </summary>
    public const int RouterOrder = -10;

    /// <summary>The router's node path — what the composer persists when Auto is selected.</summary>
    public static string RouterPath =>
        $"{ModelProviderNodeType.RootNamespace}/{RouterProviderName}/{RouterModelId}";

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
        // Companion NodeType: ModelProvider holds the credentials shared by
        // all child LanguageModel nodes. Registered together so a deployment
        // calling AddLanguageModelType wires the entire data shape (the
        // ChatClientCredentialResolver depends on both being available).
        builder.AddModelProviderType(serveFromPartition);
        // Companion NodeType: ModelTier is the registry of usage rungs a model node's
        // ModelDefinition.Tier and an agent's ModelTier point at. Same reason it is registered here —
        // a deployment that has models must be able to say what each one is FOR.
        builder.AddModelTierType(serveFromPartition);
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
            // 🧊 The mesh's SHARED resolver is warmed by whoever builds it — here. Reads are pure
            // (they never open the catalog subscription), so warming is an owner's decision rather
            // than a side effect of the first lookup. Every consumer resolves the resolver from DI,
            // so every consumer still gets a warming snapshot; and a caller that constructs its OWN
            // resolver keeps it in the pre-warm state for as long as it wants — which is what makes
            // "cold catalog" a state a test can OWN instead of race
            // (AutoRouterDispatchTest.RouterIsRecognisedAgainstAColdCatalog).
            services.TryAddSingleton(sp =>
            {
                var resolver = new ChatClientCredentialResolver(sp.GetRequiredService<IMessageHub>());
                resolver.EnsureSubscription();
                return resolver;
            });
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
            // 🚨 Plain AddSingleton (not TryAddEnumerable): TryAddEnumerable
            // dedupes by impl-type AND ServiceLifetime AND ImplementationFactory
            // — combinations that occasionally suppress the registration in
            // ways that left BuiltInLanguageModelProvider invisible to DI
            // resolution while BuiltInAgentProvider (using plain AddSingleton)
            // worked. Match the AgentProvider pattern so both follow the
            // same path.
            // 🚨 Gate the IStaticNodeProvider (feeds FindStaticNode) on !dbSynced, same as the
            // partition provider below — leaving it registered while the model-catalog partition is
            // DB-synced made the importer's inner CreateNode see the built-in catalog/Provider
            // nodes as already-present and fail "Node already exists" (prod 2026-06-11: imported
            // 4 / failed 2, incl. Provider/_Policy + Provider/Anthropic). The
            // BuiltInLanguageModelProvider singleton stays (the import source wraps it); the
            // LanguageModel/ModelProvider NodeType defs stay via AddMeshNodes. See AddAgentType.
            if (!dbSynced)
            {
                services.AddSingleton<IStaticNodeProvider>(sp => sp.GetRequiredService<BuiltInLanguageModelProvider>());
                // Partition routing — the same instance feeds the routing core's
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
    /// <para>Idempotent on (sectionName, providerName) — safe to call from
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
    /// registers via its own extension. Idempotent on (sectionName, providerName).
    /// This collection-level form is what a boot-loaded provider pack carries in its
    /// <c>MeshNodeProviderAttribute</c>'s global service configurations; the builder
    /// overload below delegates here.
    /// </summary>
    public static IServiceCollection AddLanguageModelCatalogSource(
        this IServiceCollection services,
        LanguageModelCatalogSource source)
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
    }

    /// <inheritdoc cref="AddLanguageModelCatalogSource(IServiceCollection, LanguageModelCatalogSource)"/>
    public static TBuilder AddLanguageModelCatalogSource<TBuilder>(
        this TBuilder builder,
        LanguageModelCatalogSource source)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddLanguageModelCatalogSource(source));
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
