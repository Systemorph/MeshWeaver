using MeshWeaver.Graph;
using MeshWeaver.Mesh;

namespace MeshWeaver.Courses.Configuration;

/// <summary>
/// Provides configuration for Course nodes — the root node of an interactive
/// course. The MeshNode's <c>Content</c> is a <see cref="CourseConfiguration"/>.
/// Courses are deliberately NOT partition-owning (unlike Space), so a course
/// can live in any partition.
/// </summary>
public static class CourseNodeType
{
    /// <summary>
    /// The NodeType value used to identify course nodes.
    /// </summary>
    public const string NodeType = "Course";

    /// <summary>
    /// Registers the built-in "Course" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddCourseType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Course node type.
    /// This provides HubConfiguration for nodes with nodeType="Course".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Course",
        Icon = "/static/NodeTypeIcons/rocket.svg",
        // Register the courses content types DIRECTLY on the per-course hub
        // config (not only via ConfigureDefaultNodeHub): the polymorphic
        // resolver picks the $type discriminator from the SENDING hub's
        // TypeRegistry — without the local registration, content falls back
        // to FullName on the wire and receivers can't resolve it. Mirrors
        // ThreadNodeType's AddAITypes registration.
        HubConfiguration = config =>
        {
            config.TypeRegistry.AddCoursesTypes();
            return config
                .AddMeshDataSource(s => s.WithContentType<CourseConfiguration>())
                .AddDefaultLayoutAreas();
        }
    };
}
