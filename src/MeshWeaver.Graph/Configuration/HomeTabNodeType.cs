using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for HomeTab node types — the DATA-driven extension tabs of the user
/// home's catalog row (Spaces, My Items, …). A HomeTab node's <c>Name</c> is the tab label and its
/// content maps the tab's search (<c>nodeType</c>, <c>query</c>, <c>placeholder</c>) and "+" target
/// (<c>createHref</c>); the user home's catalog area (<c>UserActivityLayoutAreas</c>) renders every
/// readable one — a plugin adds a tab (e.g. Courses → the course catalog) by shipping a node,
/// never by editing the framework.
/// </summary>
public static class HomeTabNodeType
{
    /// <summary>
    /// The NodeType value used to identify home-tab nodes — one definition, shared with the
    /// reader in <see cref="UserActivityLayoutAreas.HomeTabNodeType"/>.
    /// </summary>
    public const string NodeType = UserActivityLayoutAreas.HomeTabNodeType;

    /// <summary>
    /// Registers the built-in "HomeTab" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddHomeTabType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        // Tabs are UI config meant to appear on every user's home — public-readable by default;
        // a partition can still gate an individual tab node (visibility follows readability).
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the HomeTab node type.
    /// This provides HubConfiguration for nodes with nodeType="HomeTab".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Home Tab",
        Icon = "/static/NodeTypeIcons/layout.svg",
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource()
    };
}
