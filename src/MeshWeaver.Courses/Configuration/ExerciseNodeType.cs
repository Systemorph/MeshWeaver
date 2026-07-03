using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Courses.Configuration;

/// <summary>
/// Provides configuration for Exercise nodes — a "your turn" task inside a
/// module. The MeshNode's <c>Content</c> is an <see cref="ExerciseConfiguration"/>;
/// the exercise's code artifacts are plain Code children under the
/// <see cref="SourceSubNamespace"/> / <see cref="TestSubNamespace"/> /
/// <see cref="SolutionSubNamespace"/> sub-namespaces.
/// </summary>
public static class ExerciseNodeType
{
    /// <summary>
    /// The NodeType value used to identify exercise nodes.
    /// </summary>
    public const string NodeType = "Exercise";

    /// <summary>
    /// The sub-namespace under a module where exercises live:
    /// <c>{course}/{module}/Exercise/{n}</c>. <see cref="ExerciseAttemptNodeType.AttemptPathFor"/>
    /// walks this segment to derive the attempt path.
    /// </summary>
    public const string ExerciseSubNamespace = "Exercise";

    /// <summary>
    /// Sub-namespace for the trainee's starting point:
    /// <c>{exercise}/Source/Starter</c> (a plain Code node). Reuses
    /// <see cref="CodeNodeType.SourceSubNamespace"/> — the literal must match
    /// the Postgres satellite routing table (…/Source/… routes to the code
    /// table, intended for Code children).
    /// </summary>
    public const string SourceSubNamespace = CodeNodeType.SourceSubNamespace;

    /// <summary>
    /// Sub-namespace for the instructor's validation tests:
    /// <c>{exercise}/Test/Validation</c> (a plain Code node). Reuses
    /// <see cref="CodeNodeType.TestSubNamespace"/> — same satellite-routing
    /// rationale as <see cref="SourceSubNamespace"/>.
    /// </summary>
    public const string TestSubNamespace = CodeNodeType.TestSubNamespace;

    /// <summary>
    /// Sub-namespace for the reference solution:
    /// <c>{exercise}/Solution/Solution</c> (a plain Code node).
    /// </summary>
    public const string SolutionSubNamespace = "Solution";

    /// <summary>
    /// Node id of the starter Code node: <c>{exercise}/Source/Starter</c>.
    /// </summary>
    public const string StarterNodeId = "Starter";

    /// <summary>
    /// Node id of the validation-tests Code node: <c>{exercise}/Test/Validation</c>.
    /// </summary>
    public const string ValidationNodeId = "Validation";

    /// <summary>
    /// Node id of the reference-solution Code node: <c>{exercise}/Solution/Solution</c>.
    /// </summary>
    public const string SolutionNodeId = "Solution";

    /// <summary>
    /// Registers the built-in "Exercise" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddExerciseType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Exercise node type.
    /// This provides HubConfiguration for nodes with nodeType="Exercise".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Exercise",
        Icon = "/static/NodeTypeIcons/task-list.svg",
        // Local type registration — see CourseNodeType.CreateMeshNode.
        HubConfiguration = config =>
        {
            config.TypeRegistry.AddCoursesTypes();
            return config
                .AddMeshDataSource(s => s.WithContentType<ExerciseConfiguration>())
                .AddDefaultLayoutAreas();
        }
    };
}
