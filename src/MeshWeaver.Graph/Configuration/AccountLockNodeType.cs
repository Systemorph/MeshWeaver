using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the <see cref="AccountLock"/> node type — one record per locked (or once-locked)
/// person in the Admin partition (<see cref="AccountLock.Namespace"/>), read by the permission fold,
/// the node-type rule gate, the query boundary and the portal's admission.
/// <c>search path:Admin/_AccountLock scope:descendants nodeType:AccountLock</c> is the complete
/// register. See <c>Doc/Architecture/AccountLockout</c>.
/// </summary>
public static class AccountLockNodeType
{
    /// <summary>The node-type identifier.</summary>
    public const string NodeType = AccountLock.NodeTypeName;

    /// <summary>
    /// Registers the node type and puts <see cref="AccountLock"/> in the hub's type registry so it
    /// serializes across silos.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder AddAccountLockType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        // An access decision, not content — never a pickable node in the composer.
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<AccountLock>(nameof(AccountLock)));
        return builder;
    }

    /// <summary>The node definition for <see cref="AccountLock"/> records.</summary>
    /// <returns>The node definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Account Lock",
        IsSatelliteType = false,
        ExcludeFromContext = ImmutableHashSet.Create("search", "create", "content"),
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AccountLock>())
    };
}
