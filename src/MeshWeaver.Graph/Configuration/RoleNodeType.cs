using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Role nodes in the graph.
/// Role nodes define named permission sets (e.g., Admin, Editor, Viewer).
/// Instances can be created anywhere in the node hierarchy.
/// </summary>
public static class RoleNodeType
{
    /// <summary>
    /// The NodeType value used to identify role nodes.
    /// </summary>
    public const string NodeType = "Role";

    /// <summary>
    /// Registers the built-in "Role" MeshNode on the mesh builder
    /// and a static node provider for built-in roles.
    /// </summary>
    public static TBuilder AddRoleType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureServices(services =>
            services.AddSingleton<IStaticNodeProvider, BuiltInRolesProvider>());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Role node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Role",
        Icon = "/static/NodeTypeIcons/shield.svg",
        ExcludeFromContext = new HashSet<string> { "search" },
        AssemblyLocation = typeof(RoleNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Role>())
            .AddDefaultLayoutAreas()
    };

    // Inline SVGs for the four built-in roles — rendered directly by MeshNodeImageHelper.IsInlineSvg.
    // Each uses a 20x20 rounded-square badge matching shield.svg's visual language, with a distinct
    // hue per role so they read at a glance in menus, thumbnails, and permission pickers.
    private const string AdminIcon =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\">" +
        "<rect width=\"20\" height=\"20\" rx=\"4\" fill=\"#B91C1C\"/>" +
        "<path d=\"M10 3.2a.6.6 0 0 1 .5.28 5.2 5.2 0 0 0 4.2 2.15.6.6 0 0 1 .6.56c.1 1.02.12 2.08.04 3.16A8 8 0 0 1 10.3 16a.6.6 0 0 1-.6 0 8 8 0 0 1-5.04-6.65 16 16 0 0 1 .04-3.16.6.6 0 0 1 .6-.56A5.2 5.2 0 0 0 9.5 3.48a.6.6 0 0 1 .5-.28Zm-2.2 5.3 1.6 1.6 2.8-2.8.8.8-3.6 3.6-2.4-2.4.8-.8Z\" fill=\"white\"/>" +
        "</svg>";

    private const string EditorIcon =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\">" +
        "<rect width=\"20\" height=\"20\" rx=\"4\" fill=\"#16A34A\"/>" +
        "<path d=\"M13.47 3.97a1.6 1.6 0 0 1 2.26 2.26l-.85.85-2.26-2.26.85-.85Zm-1.56 1.56 2.26 2.26-6.9 6.9-2.83.57.57-2.83 6.9-6.9Z\" fill=\"white\"/>" +
        "</svg>";

    private const string ViewerIcon =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\">" +
        "<rect width=\"20\" height=\"20\" rx=\"4\" fill=\"#2563EB\"/>" +
        "<path d=\"M10 5.5c3.2 0 5.9 1.9 7 4.5-1.1 2.6-3.8 4.5-7 4.5S4.1 12.6 3 10c1.1-2.6 3.8-4.5 7-4.5Zm0 1.8a2.7 2.7 0 1 0 0 5.4 2.7 2.7 0 0 0 0-5.4Zm0 1.2a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3Z\" fill=\"white\"/>" +
        "</svg>";

    private const string CommenterIcon =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\">" +
        "<rect width=\"20\" height=\"20\" rx=\"4\" fill=\"#F59E0B\"/>" +
        "<path d=\"M5 6.5A1.5 1.5 0 0 1 6.5 5h7A1.5 1.5 0 0 1 15 6.5v5A1.5 1.5 0 0 1 13.5 13H9.4l-2.8 2.3A.5.5 0 0 1 5.8 15v-2A1.5 1.5 0 0 1 4.5 11.5v-5H5Zm2.5 1.75a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm2.5 0a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm2.5 0a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Z\" fill=\"white\"/>" +
        "</svg>";

    /// <summary>
    /// Provides the four built-in roles as static MeshNodes
    /// so they appear in query results regardless of scope.
    /// </summary>
    private class BuiltInRolesProvider : IStaticNodeProvider
    {
        private static readonly MeshNode[] Nodes =
        [
            // Read-only policy for the Role namespace — built-in roles are unmodifiable
            new("_Policy", "Role")
            {
                NodeType = "PartitionAccessPolicy",
                Name = "Access Policy",
                Content = new PartitionAccessPolicy
                {
                    Create = false,
                    Update = false,
                    Delete = false,
                    Comment = false,
                    Thread = false
                }
            },
            new("Admin", "Role") { Name = "Admin", NodeType = NodeType, Icon = AdminIcon, Content = Role.Admin },
            new("Editor", "Role") { Name = "Editor", NodeType = NodeType, Icon = EditorIcon, Content = Role.Editor },
            new("Viewer", "Role") { Name = "Viewer", NodeType = NodeType, Icon = ViewerIcon, Content = Role.Viewer },
            new("Commenter", "Role") { Name = "Commenter", NodeType = NodeType, Icon = CommenterIcon, Content = Role.Commenter },
        ];

        public IEnumerable<MeshNode> GetStaticNodes() => Nodes;
    }
}
