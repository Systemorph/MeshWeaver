using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // GUI-create protocol: creating an Exercise from the "+" seeds the
        // exercise node PLUS its three Code stubs (starter / validation /
        // solution) and navigates straight to the new exercise — the
        // ThreadNodeType.BuildCreate shape, no generic create form.
        Content = new NodeTypeDefinition
        {
            BuildCreate = (host, ns) => BuildCreateExercise(host.Hub, ns)
        },
        // Local type registration — see CourseNodeType.CreateMeshNode.
        HubConfiguration = config =>
        {
            config.TypeRegistry.AddCoursesTypes();
            return config
                .AddMeshDataSource(s => s.WithContentType<ExerciseConfiguration>())
                // Framework defaults + the exercise workspace as default area.
                .AddExerciseViews();
        }
    };

    /// <summary>
    /// The GUI-create pipeline for exercises: creates the Exercise node at
    /// <c>{ns}/Exercise/{id}</c> (appending the <see cref="ExerciseSubNamespace"/>
    /// segment unless the "+" was already clicked inside it), seeds the three
    /// Code stubs — <c>Source/Starter</c> (executable), <c>Test/Validation</c>
    /// and <c>Solution/Solution</c> — through the reactive
    /// <c>meshService.CreateNode</c> chain, then redirects to the new
    /// exercise's workspace. Cold end-to-end; the layout host subscribes.
    /// </summary>
    public static IObservable<UiControl?> BuildCreateExercise(
        IMessageHub hub, string ns)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Courses.ExerciseCreate");

        var exerciseNamespace = ns.EndsWith($"/{ExerciseSubNamespace}", StringComparison.Ordinal)
            ? ns
            : $"{ns}/{ExerciseSubNamespace}";
        var exerciseId = $"exercise-{Guid.NewGuid():N}"[..17];
        var exercisePath = $"{exerciseNamespace}/{exerciseId}";

        var exerciseNode = new MeshNode(exerciseId, exerciseNamespace)
        {
            Name = "New Exercise",
            NodeType = NodeType,
            State = MeshNodeState.Active,
            Content = new ExerciseConfiguration
            {
                Statement = "Describe the task for the trainee here."
            }
        };
        var starterNode = new MeshNode(StarterNodeId, $"{exercisePath}/{SourceSubNamespace}")
        {
            Name = "Starter",
            NodeType = CodeNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Code = "// The trainee's starting point.",
                IsExecutable = true
            }
        };
        var validationNode = new MeshNode(ValidationNodeId, $"{exercisePath}/{TestSubNamespace}")
        {
            Name = "Validation",
            NodeType = CodeNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Code = "// Instructor tests — throw when the trainee's submission is wrong."
            }
        };
        var solutionNode = new MeshNode(SolutionNodeId, $"{exercisePath}/{SolutionSubNamespace}")
        {
            Name = "Solution",
            NodeType = CodeNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Code = "// The reference solution."
            }
        };

        return meshService.CreateNode(exerciseNode)
            .SelectMany(_ => meshService.CreateNode(starterNode))
            .SelectMany(_ => meshService.CreateNode(validationNode))
            .SelectMany(_ => meshService.CreateNode(solutionNode))
            .Take(1)
            .Select(_ => (UiControl?)new RedirectControl($"/{exercisePath}"))
            .Catch<UiControl?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[ExerciseCreate] Seeding failed at {Path}", exercisePath);
                return Observable.Return<UiControl?>(Controls.Markdown(
                    $"**Could not create the exercise:**\n\n{ex.Message}"));
            });
    }
}
