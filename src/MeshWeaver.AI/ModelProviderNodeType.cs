using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI;

/// <summary>
/// Mesh-node type for AI model provider credentials — companion to
/// <see cref="LanguageModelNodeType"/>. One node per (user, provider) pair
/// at <c>{userId}/Model/{providerName}</c>; LanguageModel nodes live as
/// children under the provider's namespace.
///
/// <para>Two surfaces feed this:</para>
/// <list type="bullet">
///   <item><b>Static layer</b>: <see cref="BuiltInLanguageModelProvider"/>
///         emits one read-only <c>ModelProvider</c> per
///         <see cref="LanguageModelCatalogSource"/> at
///         <c>Model/{providerName}</c>, stamped from the legacy
///         IConfiguration <c>{section}:ApiKey</c> / <c>{section}:Endpoint</c>
///         entries. Existing deployments that wire credentials via
///         appsettings keep working unchanged.</item>
///   <item><b>User layer</b>: <c>ModelProviderService</c> creates
///         user-authored <c>ModelProvider</c> nodes in the user's partition
///         when they paste a key in the Models settings tab.</item>
/// </list>
///
/// <para>Owner-only by default — <see cref="CreateNodePermissionAttribute.GetPermissionForNodeType"/>
/// maps <c>ModelProvider</c> to <see cref="MeshWeaver.Mesh.Security.Permission.Api"/>,
/// the same permission that gates API tokens. The root <c>Model/</c>
/// namespace ships with a read-only <c>_Policy</c> via the static provider,
/// so user extensions must live in user namespaces.</para>
/// </summary>
public static class ModelProviderNodeType
{
    /// <summary>NodeType discriminator value.</summary>
    public const string NodeType = "ModelProvider";

    /// <summary>
    /// Conventional satellite-namespace segment for provider credential
    /// nodes — mirrors <c>_Access</c>, <c>_Thread</c>, <c>_Comment</c>.
    /// User-owned providers live at <c>{userPath}/_Provider/{providerName}</c>;
    /// organisation-shared providers at <c>{orgPath}/_Provider/{providerName}</c>;
    /// system defaults at the root <c>_Provider/{providerName}</c>. The
    /// picker / resolver query each owning path's <c>_Provider</c>
    /// subtree directly — no path-walk heuristics, no central registry.
    /// </summary>
    public const string RootNamespace = "_Provider";

    /// <summary>
    /// Node id for the per-user provider-selection node — the single node, at
    /// <c>{userPath}/_Provider/_Selection</c>, whose content is a
    /// <see cref="ModelProviderSelection"/>. See <see cref="SelectionPath"/>.
    /// </summary>
    public const string SelectionNodeId = "_Selection";

    /// <summary>
    /// NodeType discriminator for the selection node — distinct from
    /// <see cref="NodeType"/> so a <c>nodeType:ModelProvider</c> listing (e.g.
    /// <c>ModelProviderService.GetProvidersForOwner</c>) never mistakes the
    /// owner's selection node for an actual provider.
    /// </summary>
    public const string SelectionNodeType = "ModelProviderSelection";

    /// <summary>
    /// Path of the provider-selection node for an owner (user) path:
    /// <c>{ownerPath}/_Provider/_Selection</c>.
    /// </summary>
    public static string SelectionPath(string ownerPath) =>
        $"{ownerPath}/{RootNamespace}/{SelectionNodeId}";

    /// <summary>
    /// Registers the <c>ModelProvider</c> MeshNode definition + content type.
    /// Wires the same hub-level content registration the
    /// <see cref="LanguageModelNodeType"/> uses so reads through
    /// <see cref="MeshWeaver.Mesh.Services.IMeshService"/> deserialise the
    /// content into the typed <see cref="ModelProviderConfiguration"/> record.
    /// </summary>
    public static TBuilder AddModelProviderType<TBuilder>(this TBuilder builder,
        IReadOnlySet<string>? serveFromPartition = null)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddMeshNodes(CreateSelectionMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.AddAutocompleteExcludedTypes(SelectionNodeType);
        builder.ConfigureHub(config => config
            .WithType<ModelProviderConfiguration>(nameof(ModelProviderConfiguration))
            .WithType<ModelProviderSelection>(nameof(ModelProviderSelection)));
        // Mirror LanguageModelNodeType: the root <c>_Provider</c> namespace
        // gets a partition-storage provider so the routing core knows where
        // to find static ModelProvider nodes (the ones BuiltInLanguageModelProvider
        // emits from IConfiguration). Without this, namespace:_Provider
        // queries return nothing because no provider claims the partition.
        // User-partition ModelProvider nodes (rbuergi/_Provider/Anthropic
        // etc.) route through their owning partition's storage adapter —
        // no extra wiring needed for those.
        // The model catalog's provider/model CONTENT lives under the "_Provider" partition;
        // it is DB-synced together with "Model". When synced, skip the read-only in-memory
        // provider so Postgres serves "_Provider" + accepts the import's writes. See AddAgentType.
        var dbSynced = serveFromPartition is not null
            && (serveFromPartition.Contains("Model") || serveFromPartition.Contains(RootNamespace));
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<BuiltInLanguageModelProvider>();
            if (!dbSynced)
                services.AddSingleton<IPartitionStorageProvider>(sp =>
                    new StaticNodePartitionStorageProvider(
                        RootNamespace,
                        sp.GetRequiredService<BuiltInLanguageModelProvider>(),
                        description: "Built-in model provider catalog (read-only)."));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// MeshNode definition for <c>nodeType:ModelProvider</c>.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Model Provider",
        Icon = "/static/NodeTypeIcons/key.svg",
        // Treated as a regular content type. Permission gating happens in
        // CreateNodePermissionAttribute.GetPermissionForNodeType → Permission.Api.
        IsSatelliteType = false,
        // Creatable: an admin can author a ModelProvider node directly in a space
        // (e.g. Systemorph/_Provider/AzureFoundry). Still hidden from search.
        ExcludeFromContext = new HashSet<string> { "search" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ModelProviderConfiguration>())
    };

    /// <summary>
    /// MeshNode definition for the per-user provider-selection node
    /// (<see cref="SelectionNodeType"/>). Distinct type so it's creatable via
    /// <c>CreateNode</c> + deserialises its <see cref="ModelProviderSelection"/>
    /// content, yet never shows up in <c>nodeType:ModelProvider</c> listings.
    /// </summary>
    public static MeshNode CreateSelectionMeshNode() => new(SelectionNodeType)
    {
        Name = "Model Provider Selection",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ModelProviderSelection>())
    };
}
