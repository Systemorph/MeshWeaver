using MeshWeaver.Courses.Configuration;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;

namespace MeshWeaver.Courses;

/// <summary>
/// Entry point for the MeshWeaver Courses facility: interactive courses built
/// from four node types — <c>Course</c> → <c>Module</c> → <c>Exercise</c>
/// (with plain Markdown/Code children for theory, examples, starter, tests and
/// solutions) plus the per-trainee <c>ExerciseAttempt</c> fork with its
/// stream-update validation control plane.
/// </summary>
public static class CoursesConfigurationExtensions
{
    /// <summary>
    /// Registers the Courses facility on the mesh builder: the four node types
    /// and the courses content types on the mesh hub + every per-node hub (so
    /// course content round-trips through polymorphic JSON across routing,
    /// mesh and per-node hubs — mirrors <c>AddAI</c>).
    /// </summary>
    public static TBuilder AddCourses<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => (TBuilder)builder
            .AddCourseType()
            .AddModuleType()
            .AddExerciseType()
            .AddExerciseAttemptType()
            .ConfigureHub(config =>
            {
                config.TypeRegistry.AddCoursesTypes();
                return config;
            })
            .ConfigureDefaultNodeHub(config =>
            {
                config.TypeRegistry.AddCoursesTypes();
                return config;
            });

    /// <summary>
    /// Registers every Courses content type (course/module/exercise/attempt
    /// records, the attempt-status enum and the internal validation trigger)
    /// on the type registry with SHORT names so they serialise correctly
    /// across the routing, mesh and per-node hubs.
    /// </summary>
    /// <param name="typeRegistry">The type registry to populate.</param>
    /// <returns>The same type registry, for chaining.</returns>
    public static ITypeRegistry AddCoursesTypes(this ITypeRegistry typeRegistry)
        => typeRegistry
            .WithType(typeof(CourseConfiguration), nameof(CourseConfiguration))
            .WithType(typeof(ModuleConfiguration), nameof(ModuleConfiguration))
            .WithType(typeof(ExerciseConfiguration), nameof(ExerciseConfiguration))
            .WithType(typeof(ExerciseAttemptStatus), nameof(ExerciseAttemptStatus))
            .WithType(typeof(AttemptStatus), nameof(AttemptStatus))
            // Internal validation-dispatch trigger — the watcher posts it to the
            // per-attempt hub's own address; registered so the routing /
            // serialisation pipeline resolves the type name even for self-post.
            .WithType(typeof(DispatchValidationTrigger), nameof(DispatchValidationTrigger));
}
