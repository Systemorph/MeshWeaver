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
    /// Sub-namespace under a module for theory blocks (plain Markdown
    /// children): <c>{module}/Theory/{n}</c>. The module page embeds them in
    /// <see cref="MeshNode.Order"/> order.
    /// </summary>
    public const string TheorySubNamespace = "Theory";

    /// <summary>
    /// Sub-namespace under a module for worked examples (plain Code children):
    /// <c>{module}/Example/{n}</c>.
    /// </summary>
    public const string ExampleSubNamespace = "Example";

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
                // Framework defaults + the module page (Content) as the default
                // area — summary, theory/example embeds, exercise tabs, nav.
                .AddModuleViews();
        }
    };
}
