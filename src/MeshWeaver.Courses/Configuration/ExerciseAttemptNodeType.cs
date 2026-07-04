using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Courses.Configuration;

/// <summary>
/// Provides configuration for ExerciseAttempt nodes — one trainee's fork of
/// one exercise, living in the trainee's own partition. The MeshNode's
/// <c>Content</c> is an <see cref="ExerciseAttemptStatus"/>; the trainee's
/// working copy of the code is the plain Code child at
/// <c>{attempt}/Source/Code</c>.
///
/// <para>The per-attempt hub installs the validation watcher
/// (<see cref="ExerciseValidationWatcher"/>): flipping
/// <see cref="ExerciseAttemptStatus.ValidationRequestedAt"/> via
/// <c>GetMeshNodeStream(attemptPath).Update(...)</c> runs the trainee's code
/// concatenated with the exercise's validation tests on the kernel and stamps
/// the pass/fail outcome back onto the attempt.</para>
/// </summary>
public static class ExerciseAttemptNodeType
{
    /// <summary>
    /// The NodeType value used to identify exercise-attempt nodes.
    /// </summary>
    public const string NodeType = "ExerciseAttempt";

    /// <summary>
    /// The sub-namespace of the trainee's partition under which attempts live:
    /// <c>{user}/Courses/{Escape(coursePath)}/{moduleId}/{exerciseId}</c>.
    /// </summary>
    public const string CoursesSubNamespace = "Courses";

    /// <summary>
    /// Node id of the trainee's working-copy Code child:
    /// <c>{attempt}/Source/Code</c>.
    /// </summary>
    public const string AttemptCodeNodeId = "Code";

    /// <summary>
    /// Registers the built-in "ExerciseAttempt" MeshNode on the mesh builder.
    /// Attempts are per-trainee working state — excluded from autocomplete,
    /// search and the create menu (clone of the Code node type's exclusions).
    /// </summary>
    public static TBuilder AddExerciseAttemptType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the ExerciseAttempt node type.
    /// This provides HubConfiguration for nodes with nodeType="ExerciseAttempt",
    /// including the validation watcher and its dispatch handler.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Exercise Attempt",
        Icon = "/static/NodeTypeIcons/checkmark.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        // Local type registration — see CourseNodeType.CreateMeshNode.
        HubConfiguration = config =>
        {
            config.TypeRegistry.AddCoursesTypes();
            return config
                .AddMeshDataSource(s => s.WithContentType<ExerciseAttemptStatus>())
                .AddDefaultLayoutAreas()
                // Validation dispatch runs on the per-attempt hub's ActionBlock —
                // the watcher's Subscribe callback ONLY posts this trigger (never
                // updates / queries inline; see ExerciseValidationWatcher).
                .WithHandler<DispatchValidationTrigger>(ExerciseValidationWatcher.HandleDispatchValidation)
                .WithInitialization(ExerciseValidationWatcher.Install);
        }
    };

    /// <summary>
    /// Maps an exercise path to the trainee's attempt path. An exercise lives
    /// at <c>{coursePath}/{moduleId}/Exercise/{exerciseId}</c>; the attempt
    /// lives at
    /// <c>{userHome}/Courses/{Escape(coursePath)}/{moduleId}/{exerciseId}</c>
    /// (the course path is flattened with <see cref="PathEscaping.Escape"/> so
    /// the attempt tree stays shallow regardless of where the course lives).
    /// </summary>
    /// <param name="userHome">The trainee's partition root (user home).</param>
    /// <param name="exercisePath">Full path of the Exercise MeshNode.</param>
    /// <exception cref="ArgumentException">
    /// When <paramref name="userHome"/> is empty or
    /// <paramref name="exercisePath"/> does not match the
    /// <c>{coursePath}/{moduleId}/Exercise/{exerciseId}</c> shape.
    /// </exception>
    public static string AttemptPathFor(string userHome, string exercisePath)
    {
        if (string.IsNullOrWhiteSpace(userHome))
            throw new ArgumentException("User home must not be empty.", nameof(userHome));
        if (string.IsNullOrWhiteSpace(exercisePath))
            throw new ArgumentException("Exercise path must not be empty.", nameof(exercisePath));

        var segments = exercisePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Minimum shape: {course}/{module}/Exercise/{n} — four segments with
        // the Exercise sub-namespace second-to-last. Courses may nest deeper
        // ({anywhere}/{courseId}); everything before {module} is the course path.
        if (segments.Length < 4
            || !string.Equals(segments[^2], ExerciseNodeType.ExerciseSubNamespace, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Exercise path '{exercisePath}' does not match the expected " +
                $"'{{coursePath}}/{{moduleId}}/{ExerciseNodeType.ExerciseSubNamespace}/{{exerciseId}}' shape.",
                nameof(exercisePath));

        var exerciseId = segments[^1];
        var moduleId = segments[^3];
        var coursePath = string.Join('/', segments[..^3]);
        return $"{userHome}/{CoursesSubNamespace}/{PathEscaping.Escape(coursePath)}/{moduleId}/{exerciseId}";
    }

    /// <summary>
    /// Forks an exercise for the calling user: reads the starter Code node at
    /// <c>{exercisePath}/Source/Starter</c>, creates the attempt node
    /// (<see cref="ExerciseAttemptStatus"/> with
    /// <see cref="AttemptStatus.InProgress"/>) at
    /// <see cref="AttemptPathFor"/>, then creates the attempt's working-copy
    /// Code child at <c>{attempt}/Source/Code</c> copying the starter's
    /// <c>CodeConfiguration</c> (with <c>IsExecutable = true</c>).
    ///
    /// <para>Cold and reactive end-to-end — the creates only run on
    /// Subscribe; the caller subscribes (or awaits in tests via the
    /// sanctioned test-edge bridge). Emits the attempt path.</para>
    /// </summary>
    /// <param name="hub">The hub of the caller (resolves the viewer identity + IMeshService).</param>
    /// <param name="exercisePath">Full path of the Exercise MeshNode to fork.</param>
    public static IObservable<string> StartAttempt(IMessageHub hub, string exercisePath)
        => Observable.Defer(() =>
        {
            // Resolve the viewer like ThreadNodeType.BuildCreate does: the
            // per-delivery context first, the circuit context as fallback.
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            var userHome = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
            if (string.IsNullOrEmpty(userHome))
                return Observable.Throw<string>(new InvalidOperationException(
                    "StartAttempt requires a signed-in user (no AccessContext ObjectId available)."));

            var attemptPath = AttemptPathFor(userHome, exercisePath);
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var starterPath =
                $"{exercisePath}/{ExerciseNodeType.SourceSubNamespace}/{ExerciseNodeType.StarterNodeId}";

            return hub.GetMeshNode(starterPath)
                // Missing / non-Code starter degrades to an empty working copy —
                // the fork still succeeds, the trainee starts from a blank editor.
                .Select(starterNode => starterNode?.Content as CodeConfiguration ?? new CodeConfiguration())
                .SelectMany(starter =>
                {
                    var attemptNode = MeshNode.FromPath(attemptPath) with
                    {
                        Name = $"Attempt {attemptPath.Split('/')[^1]}",
                        NodeType = NodeType,
                        State = MeshNodeState.Active,
                        Content = new ExerciseAttemptStatus
                        {
                            ExercisePath = exercisePath,
                            Status = AttemptStatus.InProgress
                        }
                    };
                    var codeNode = MeshNode.FromPath(
                        $"{attemptPath}/{ExerciseNodeType.SourceSubNamespace}/{AttemptCodeNodeId}") with
                    {
                        Name = "Code",
                        NodeType = CodeNodeType.NodeType,
                        State = MeshNodeState.Active,
                        Content = starter with { IsExecutable = true }
                    };
                    return meshService.CreateNode(attemptNode)
                        .SelectMany(_ => meshService.CreateNode(codeNode))
                        .Select(_ => attemptPath);
                });
        });
}
