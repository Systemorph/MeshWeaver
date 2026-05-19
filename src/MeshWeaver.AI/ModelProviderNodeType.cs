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
///   <item><b>User layer</b>: <see cref="ModelProviderService"/> creates
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
    /// Registers the <c>ModelProvider</c> MeshNode definition + content type.
    /// Wires the same hub-level content registration the
    /// <see cref="LanguageModelNodeType"/> uses so reads through
    /// <see cref="MeshWeaver.Mesh.Services.IMeshService"/> deserialise the
    /// content into the typed <see cref="ModelProviderConfiguration"/> record.
    /// </summary>
    public static TBuilder AddModelProviderType<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config
            .WithType<ModelProviderConfiguration>(nameof(ModelProviderConfiguration)));
        // Mirror LanguageModelNodeType: the root <c>_Provider</c> namespace
        // gets a partition-storage provider so the routing core knows where
        // to find static ModelProvider nodes (the ones BuiltInLanguageModelProvider
        // emits from IConfiguration). Without this, namespace:_Provider
        // queries return nothing because no provider claims the partition.
        // User-partition ModelProvider nodes (rbuergi/_Provider/Anthropic
        // etc.) route through their owning partition's storage adapter —
        // no extra wiring needed for those.
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<BuiltInLanguageModelProvider>();
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
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ModelProviderConfiguration>())
    };
}
