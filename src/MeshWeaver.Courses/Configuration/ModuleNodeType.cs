using MeshWeaver.Graph;
using MeshWeaver.Mesh;

namespace MeshWeaver.Courses.Configuration;

/// <summary>
/// Provides configuration for Module nodes — one module of a course. The
/// MeshNode's <c>Content</c> is a <see cref="ModuleConfiguration"/>. Theory
/// blocks are plain Markdown children, worked examples plain Code children,
/// and exercises live under <c>{module}/Exercise/{n}</c>.
/// </summary>
public static class ModuleNodeType
{
    /// <summary>
    /// The NodeType value used to identify module nodes.
    /// </summary>
    public const string NodeType = "Module";

    /// <summary>
    /// Registers the built-in "Module" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddModuleType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Module node type.
    /// This provides HubConfiguration for nodes with nodeType="Module".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Module",
        Icon = "/static/NodeTypeIcons/folder.svg",
        // Local type registration — see CourseNodeType.CreateMeshNode.
        HubConfiguration = config =>
        {
            config.TypeRegistry.AddCoursesTypes();
            return config
                .AddMeshDataSource(s => s.WithContentType<ModuleConfiguration>())
                .AddDefaultLayoutAreas();
        }
    };
}
