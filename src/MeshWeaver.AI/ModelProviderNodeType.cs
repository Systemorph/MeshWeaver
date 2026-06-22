using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
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
    /// Namespace the PLATFORM model catalog lives under — the Admin partition's
    /// <c>Admin/Provider</c> sub-namespace (schema <c>admin</c>). System default
    /// providers live at <c>Admin/Provider/{providerName}</c>; organisation-shared
    /// providers at <c>{orgPath}/Admin/Provider/{providerName}</c>. A user's OWN
    /// providers live in their dotfile namespace instead —
    /// <c>{userPath}/_Memex/{providerName}</c> (see <see cref="UserNamespace"/>).
    /// The picker / resolver query each owning path's subtree directly — no
    /// path-walk heuristics, no central registry.
    ///
    /// <para>NOT to be confused with the unrelated GitSync user-credential
    /// <c>{user}/_Provider</c> namespace — that is GitHub OAuth credentials, a
    /// different satellite owned by <c>MeshWeaver.GitSync</c>.</para>
    /// </summary>
    public const string RootNamespace = "Admin/Provider";

    /// <summary>
    /// Per-user satellite namespace for the user's OWN providers, models, and
    /// selection — the hidden "dotfile" namespace
    /// (<see cref="ThreadComposerNodeType.MemexDefaultsNamespace"/>, <c>_Memex</c>)
    /// for per-user Memex defaults. User-owned provider /
    /// model nodes live at <c>{userPath}/_Memex/{providerName}</c> and
    /// <c>{userPath}/_Memex/{providerName}/{modelId}</c>; the user's selection at
    /// <c>{userPath}/_Memex/_Selection</c>.
    ///
    /// <para>Distinct from <see cref="RootNamespace"/> (<c>Admin/Provider</c>), which
    /// holds the SYSTEM catalog in the Admin partition and org/context-SHARED providers at
    /// <c>{orgPath}/Admin/Provider/…</c>. A user's personal credentials are theirs,
    /// so they belong in their dotfile namespace, not a shared satellite. The
    /// picker / resolver union BOTH namespaces (see the model queries) so a user
    /// sees the system catalog, any shared providers, and their own.</para>
    /// </summary>
    public const string UserNamespace = ThreadComposerNodeType.MemexDefaultsNamespace; // "_Memex"

    /// <summary>
    /// Node id for the per-user provider-selection node — the single node, at
    /// <c>{userPath}/_Memex/_Selection</c>, whose content is a
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
    /// <c>{ownerPath}/_Memex/_Selection</c>.
    /// </summary>
    public static string SelectionPath(string ownerPath) =>
        $"{ownerPath}/{UserNamespace}/{SelectionNodeId}";

    /// <summary>
    /// The owner's personal provider/model namespace path:
    /// <c>{ownerPath}/_Memex</c>. Picker + resolver query this (scope:descendants)
    /// to surface a user's OWN providers and models.
    /// </summary>
    public static string UserNamespacePath(string ownerPath) =>
        $"{ownerPath}/{UserNamespace}";

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
        // Mirror LanguageModelNodeType: the <c>Admin/Provider</c> namespace
        // gets a partition-storage provider so the routing core knows where
        // to find static ModelProvider nodes (the ones BuiltInLanguageModelProvider
        // emits from IConfiguration). Without this, namespace:Admin/Provider
        // queries return nothing because no provider claims the partition.
        // Context-partition ModelProvider nodes (rbuergi/Admin/Provider/Anthropic
        // etc.) route through their owning partition's storage adapter —
        // no extra wiring needed for those.
        // The model catalog's provider/model CONTENT lives under the "Admin/Provider" namespace;
        // it is DB-synced together with "Model". When synced, skip the read-only in-memory
        // provider so Postgres serves "Admin/Provider" + accepts the import's writes. See AddAgentType.
        var dbSynced = serveFromPartition is not null
            && (serveFromPartition.Contains("Model") || serveFromPartition.Contains(RootNamespace));
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<BuiltInLanguageModelProvider>();
            // Always generate a default (empty) {user}/_Memex/_Selection at User
            // onboarding so the chat picker's selection read RESOLVES instead of
            // generating a routing NotFound the GUI re-issues on a loop — the
            // resubscribe-storm that starved the circuit until unrelated
            // SubscribeRequests never completed (sglauser deadlock, 2026-06-09; same
            // class as the 2026-06-08 storm). Empty selection == default catalog
            // (root + context + nodeType), the existing behaviour. Mirrors
            // ThreadComposerSeedHandler / the _Thread/ThreadComposer seed.
            services.AddSingleton<INodePostCreationHandler>(_ => new ModelProviderSelectionSeedHandler());
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
        // (e.g. Systemorph/Admin/Provider/AzureFoundry). Still hidden from search.
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

    /// <summary>
    /// Seeds the per-user default <c>{user}/_Memex/_Selection</c> node (an empty
    /// <see cref="ModelProviderSelection"/>) when a <c>User</c> partition is created.
    /// Returned from <see cref="GetAdditionalNodes"/> so it persists directly alongside
    /// the user (no hub round-trip) — the "always generate the default state" step that
    /// keeps the chat picker's selection read from ever hitting a routing NotFound,
    /// whose GUI-driven re-issue loop starved the circuit and hung unrelated
    /// SubscribeRequests (the sglauser deadlock). Mirrors <c>ThreadComposerSeedHandler</c>.
    /// </summary>
    private sealed class ModelProviderSelectionSeedHandler : INodePostCreationHandler
    {
        public string NodeType => UserNodeType.NodeType; // "User"

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
            => System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            var userPath = !string.IsNullOrEmpty(createdNode.Path) ? createdNode.Path : createdNode.Id;
            if (string.IsNullOrEmpty(userPath))
                yield break;

            yield return new MeshNode(SelectionNodeId, $"{userPath}/{UserNamespace}")
            {
                NodeType = SelectionNodeType,
                Name = "Model Provider Selection",
                Content = new ModelProviderSelection(),
            };
        }
    }
}
