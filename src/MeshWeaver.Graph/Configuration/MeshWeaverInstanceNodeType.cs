using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the <see cref="MeshWeaverInstance"/> node type — the logical App ID a MeshWeaver
/// installation identifies itself with, plus the admin-owned <see cref="PluginGrant"/> that says
/// what it may pull.
///
/// <para>Layout mirrors <see cref="ApiTokenNodeType"/>, deliberately:</para>
/// <list type="bullet">
/// <item><c>{userId}/MeshWeaverInstance/{instanceId}</c> — the instance, in its OWNER's partition.
/// Self-service: any user may register one (creation maps to <c>Permission.Api</c>).</item>
/// <item><c>MeshWeaverInstance/{keyHashPrefix}</c> — the global routing index from a presented key
/// to its instance node. Written under System identity; the global namespace is not user-writable.</item>
/// <item><c>Admin/_PluginGrant/{instanceId}</c> — what the instance may pull. In the <b>Admin</b>
/// partition so the instance's owner cannot grant themselves anything.</item>
/// </list>
///
/// <para>🚨 The three-way split IS the security model. Collapsing the grant onto the instance node
/// would put an access decision inside a record its subject can write.</para>
/// </summary>
public static class MeshWeaverInstanceNodeType
{
    /// <summary>The node-type identifier for registered-installation nodes.</summary>
    public const string NodeType = "MeshWeaverInstance";

    /// <summary>The node-type identifier for grant nodes.</summary>
    public const string GrantNodeType = "PluginGrant";

    /// <summary>Global namespace holding the key-hash → instance routing index.</summary>
    public const string IndexNamespace = "MeshWeaverInstance";

    /// <summary>Namespace (under the Admin partition) holding grant nodes.</summary>
    public const string GrantNamespace = "Admin/_PluginGrant";

    /// <summary>Path of the grant node for <paramref name="instanceId"/>.</summary>
    public static string GrantPath(string instanceId) => $"{GrantNamespace}/{instanceId}";

    /// <summary>
    /// Registers both node types on the mesh builder and puts their content types in the hub's
    /// type registry so they serialize across silos (without this, a cross-silo create fails with
    /// "NodeType 'MeshWeaverInstance' is not registered" — same trap ApiToken hit).
    /// </summary>
    public static TBuilder AddMeshWeaverInstanceType<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode(), CreateGrantMeshNode());
        // An instance is infrastructure identity, not content — keep both out of autocomplete so
        // they never surface as pickable nodes in the composer.
        builder.AddAutocompleteExcludedTypes(NodeType, GrantNodeType);
        builder.ConfigureHub(config => config
            .WithType<MeshWeaverInstance>(nameof(MeshWeaverInstance))
            .WithType<MeshWeaverInstanceIndex>(nameof(MeshWeaverInstanceIndex))
            .WithType<PluginGrant>(nameof(PluginGrant))
            .WithType<PluginGrantEntry>(nameof(PluginGrantEntry)));
        return builder;
    }

    /// <summary>The <see cref="MeshWeaverInstance"/> node definition (instances + their index).</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "MeshWeaver Instance",
        // Regular content type, like ApiToken: MainNode = the owning userId on each instance row
        // plus the per-user-partition own-scope shortcut in RlsNodeValidator gates Create/Read.
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<MeshWeaverInstance>()
                .WithContentType<MeshWeaverInstanceIndex>())
    };

    /// <summary>The <see cref="PluginGrant"/> node definition. Lives under the Admin partition, so
    /// the partition's own access control — not a node-type permission — is what keeps ordinary
    /// users out.</summary>
    public static MeshNode CreateGrantMeshNode() => new(GrantNodeType)
    {
        Name = "Plugin Grant",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<PluginGrant>())
    };
}
