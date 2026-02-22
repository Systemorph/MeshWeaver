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
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Role node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Role",
        Icon = "Shield",
        AssemblyLocation = typeof(RoleNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Role>())
            .AddDefaultLayoutAreas()
    };

    /// <summary>
    /// Provides the four built-in roles as static MeshNodes
    /// so they appear in query results regardless of scope.
    /// </summary>
    private class BuiltInRolesProvider : IStaticNodeProvider
    {
        private static readonly MeshNode[] Nodes =
        [
            new("Admin", "Role") { Name = "Administrator", NodeType = NodeType, Content = Role.Admin },
            new("Editor", "Role") { Name = "Editor", NodeType = NodeType, Content = Role.Editor },
            new("Viewer", "Role") { Name = "Viewer", NodeType = NodeType, Content = Role.Viewer },
            new("Commenter", "Role") { Name = "Commenter", NodeType = NodeType, Content = Role.Commenter },
        ];

        public IEnumerable<MeshNode> GetStaticNodes() => Nodes;
    }
}
