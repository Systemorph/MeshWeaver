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

    /// <summary>The node-type identifier for registration bootstrap keys
    /// (<see cref="RegistrationKey"/>) and their index nodes. The key node lives in the minting
    /// admin's partition (<c>{userId}/RegistrationKey/{id}</c>); the index shares
    /// <see cref="IndexNamespace"/> with instance-key indexes — content type tells them apart.</summary>
    public const string RegistrationKeyNodeType = "RegistrationKey";

    /// <summary>The node-type identifier for the CONSUMER's consent record
    /// (<see cref="InstanceConsent"/>) — the privacy statement and platform terms a platform admin
    /// of this installation accepted before it registers itself at a registry.</summary>
    public const string ConsentNodeType = "InstanceConsent";

    /// <summary>The consent record's namespace: the Admin partition, so only a global admin can
    /// give (or withdraw, by deleting it) the deployment's consent.</summary>
    public const string ConsentNamespace = "Admin";

    /// <summary>The consent record's id — one per instance.</summary>
    public const string ConsentId = "InstanceConsent";

    /// <summary>Path of the consent record.</summary>
    public const string ConsentPath = $"{ConsentNamespace}/{ConsentId}";

    /// <summary>
    /// The node-type identifier for BUILD principals (<see cref="BuildPrincipal"/>) — the repository
    /// rules a GitHub Actions OIDC token is checked against (#2483). Lives in the <b>Admin</b>
    /// partition (<see cref="BuildPrincipal.Namespace"/>) for the same reason
    /// <see cref="PluginGrant"/> does: the subject of an access decision must not be able to write
    /// the decision, and the Admin partition IS the global-admin gate.
    /// </summary>
    public const string BuildPrincipalNodeType = "BuildPrincipal";

    /// <summary>
    /// Registers both node types on the mesh builder and puts their content types in the hub's
    /// type registry so they serialize across silos (without this, a cross-silo create fails with
    /// "NodeType 'MeshWeaverInstance' is not registered" — same trap ApiToken hit).
    /// </summary>
    public static TBuilder AddMeshWeaverInstanceType<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(
            CreateMeshNode(), CreateGrantMeshNode(), CreateRegistrationKeyMeshNode(), CreateConsentMeshNode(),
            CreateBuildPrincipalMeshNode());
        // An instance is infrastructure identity, not content — keep all of these out of
        // autocomplete so they never surface as pickable nodes in the composer.
        builder.AddAutocompleteExcludedTypes(
            NodeType, GrantNodeType, RegistrationKeyNodeType, ConsentNodeType, BuildPrincipalNodeType);
        builder.ConfigureHub(config => config
            .WithType<MeshWeaverInstance>(nameof(MeshWeaverInstance))
            .WithType<MeshWeaverInstanceIndex>(nameof(MeshWeaverInstanceIndex))
            .WithType<PluginGrant>(nameof(PluginGrant))
            .WithType<PluginGrantEntry>(nameof(PluginGrantEntry))
            .WithType<RegistrationKey>(nameof(RegistrationKey))
            .WithType<RegistrationKeyIndex>(nameof(RegistrationKeyIndex))
            .WithType<InstanceConsent>(nameof(InstanceConsent))
            .WithType<BuildPrincipal>(nameof(BuildPrincipal)));
        return builder;
    }

    /// <summary>
    /// The <see cref="BuildPrincipal"/> node definition — one node per repository this mesh trusts
    /// to act as a build, in the Admin partition so only a global admin can create or revoke one.
    /// <c>search nodeType:BuildPrincipal</c> is the complete list, which is precisely what the Entra
    /// federated credentials it replaces could not answer.
    /// </summary>
    /// <returns>The node definition.</returns>
    public static MeshNode CreateBuildPrincipalMeshNode() => new(BuildPrincipalNodeType)
    {
        Name = "Build Principal",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<BuildPrincipal>())
    };

    /// <summary>The <see cref="InstanceConsent"/> node definition. One record in the Admin
    /// partition; the partition's access control is what keeps everyone but a global admin from
    /// consenting on the deployment's behalf.</summary>
    public static MeshNode CreateConsentMeshNode() => new(ConsentNodeType)
    {
        Name = "Instance Consent",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<InstanceConsent>())
    };

    /// <summary>The <see cref="MeshWeaverInstance"/> node definition (instances + their index).</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "MeshWeaver Instance",
        // Regular content type, like ApiToken: MainNode = the owning userId on each instance row
        // plus the per-user-partition own-scope shortcut in RlsNodeValidator gates Create/Read.
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<MeshWeaverInstance>()
                .WithContentType<MeshWeaverInstanceIndex>())
    };

    /// <summary>The <see cref="RegistrationKey"/> node definition (keys + their index rows). Same
    /// shape as the instance node type: owner-partition nodes plus a System-written index.</summary>
    public static MeshNode CreateRegistrationKeyMeshNode() => new(RegistrationKeyNodeType)
    {
        Name = "Registration Key",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<RegistrationKey>()
                .WithContentType<RegistrationKeyIndex>())
    };

    /// <summary>The <see cref="PluginGrant"/> node definition. Lives under the Admin partition, so
    /// the partition's own access control — not a node-type permission — is what keeps ordinary
    /// users out.</summary>
    public static MeshNode CreateGrantMeshNode() => new(GrantNodeType)
    {
        Name = "Plugin Grant",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<PluginGrant>())
    };
}
