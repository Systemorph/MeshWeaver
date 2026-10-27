using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the <see cref="GrantRule"/> node type — a declared, type-scoped grant in the Admin
/// partition (<see cref="GrantRule.Namespace"/>), evaluated by the permission fold and the Postgres
/// projection, never materialised as <c>AccessAssignment</c> rows.
/// <c>search path:Admin/_GrantRule scope:descendants nodeType:GrantRule</c> is the complete list.
/// See <c>Doc/Architecture/AccessControl</c> → "Declared grant rules".
/// </summary>
public static class GrantRuleNodeType
{
    /// <summary>The node-type identifier.</summary>
    public const string NodeType = GrantRule.NodeTypeName;

    /// <summary>
    /// Registers the node type and puts <see cref="GrantRule"/> in the hub's type registry so it
    /// serializes across silos.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder AddGrantRuleType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        // An access decision, not content — never a pickable node in the composer.
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<GrantRule>(nameof(GrantRule)));
        return builder;
    }

    /// <summary>The node definition for <see cref="GrantRule"/> records.</summary>
    /// <returns>The node definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Grant Rule",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<GrantRule>())
    };
}
