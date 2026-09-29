using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the <see cref="ServiceIdentity"/> node type — the record of a non-person principal, in
/// the Admin partition (<see cref="ServiceIdentity.Namespace"/>), so only a global admin can create
/// or revoke one. <c>search nodeType:ServiceIdentity namespace:Admin/_ServiceIdentity</c> is the
/// complete list of services this mesh lets authenticate. See <c>Doc/Architecture/ServiceIdentities</c>.
/// </summary>
public static class ServiceIdentityNodeType
{
    /// <summary>The node-type identifier.</summary>
    public const string NodeType = ServiceIdentity.NodeType;

    /// <summary>
    /// Registers the node type and puts <see cref="ServiceIdentity"/> in the hub's type registry so it
    /// serializes across silos.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder AddServiceIdentityType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        // Infrastructure identity, not content — never a pickable node in the composer.
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<ServiceIdentity>(nameof(ServiceIdentity)));
        return builder;
    }

    /// <summary>The node definition for <see cref="ServiceIdentity"/> records.</summary>
    /// <returns>The node definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Service Identity",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ServiceIdentity>())
    };
}
