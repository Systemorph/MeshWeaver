using System;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// Shared fixture for Courses integration tests: monolith mesh with
/// <c>AddCourses()</c> + sample users (so the DevLogin admin's home partition
/// "Roland" exists for attempt forks), a layout-enabled client whose type
/// registry knows the courses content types, and a seeding helper that builds
/// a Course → Module → Exercise tree with starter / validation / solution
/// Code children.
/// </summary>
public abstract class CoursesTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The partition the seeded courses live in.</summary>
    protected const string CoursePartition = "rbuergi";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddCourses()
            .AddSampleUsers();

    /// <summary>
    /// Client with layout/data support (for <c>GetWorkspace().GetMeshNodeStream</c>)
    /// and the courses types on its registry so typed content round-trips.
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddCoursesTypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// The seeded course structure's paths.
    /// </summary>
    protected sealed record SeededExercise(
        string CoursePath, string ModulePath, string ExercisePath,
        string StarterPath, string ValidationPath, string SolutionPath);

    /// <summary>
    /// Seeds a Course → Module → Exercise tree (unique ids per call) with the
    /// three Code children. <paramref name="starterCode"/> is the trainee's
    /// starting point; <paramref name="validationCode"/> the instructor tests
    /// concatenated after the trainee code by the validation watcher.
    /// </summary>
    protected async Task<SeededExercise> SeedExercise(
        string starterCode, string validationCode, string solutionCode = "// solution")
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var courseId = $"course-{Guid.NewGuid():N}"[..14];
        var coursePath = $"{CoursePartition}/{courseId}";
        var modulePath = $"{coursePath}/Module1";
        var exerciseNamespace = $"{modulePath}/{ExerciseNodeType.ExerciseSubNamespace}";
        var exercisePath = $"{exerciseNamespace}/Ex1";

        await mesh.CreateNode(new MeshNode(courseId, CoursePartition)
        {
            Name = "Test Course",
            NodeType = CourseNodeType.NodeType,
            Content = new CourseConfiguration
            {
                Description = "A course used by the integration tests.",
                TutorInstructions = "Give hints, never the solution."
            }
        }).Should().Within(30.Seconds()).Emit();

        await mesh.CreateNode(new MeshNode("Module1", coursePath)
        {
            Name = "Module 1",
            NodeType = ModuleNodeType.NodeType,
            Content = new ModuleConfiguration { Summary = "First module." }
        }).Should().Within(30.Seconds()).Emit();

        await mesh.CreateNode(new MeshNode("Ex1", exerciseNamespace)
        {
            Name = "Exercise 1",
            NodeType = ExerciseNodeType.NodeType,
            Content = new ExerciseConfiguration
            {
                Statement = "Make x equal 42.",
                Difficulty = 1
            }
        }).Should().Within(30.Seconds()).Emit();

        var starterPath = $"{exercisePath}/{ExerciseNodeType.SourceSubNamespace}/{ExerciseNodeType.StarterNodeId}";
        await mesh.CreateNode(new MeshNode(
            ExerciseNodeType.StarterNodeId,
            $"{exercisePath}/{ExerciseNodeType.SourceSubNamespace}")
        {
            Name = "Starter",
            NodeType = CodeNodeType.NodeType,
            Content = new CodeConfiguration { Code = starterCode, IsExecutable = true }
        }).Should().Within(30.Seconds()).Emit();

        var validationPath = $"{exercisePath}/{ExerciseNodeType.TestSubNamespace}/{ExerciseNodeType.ValidationNodeId}";
        await mesh.CreateNode(new MeshNode(
            ExerciseNodeType.ValidationNodeId,
            $"{exercisePath}/{ExerciseNodeType.TestSubNamespace}")
        {
            Name = "Validation",
            NodeType = CodeNodeType.NodeType,
            Content = new CodeConfiguration { Code = validationCode }
        }).Should().Within(30.Seconds()).Emit();

        var solutionPath = $"{exercisePath}/{ExerciseNodeType.SolutionSubNamespace}/{ExerciseNodeType.SolutionNodeId}";
        await mesh.CreateNode(new MeshNode(
            ExerciseNodeType.SolutionNodeId,
            $"{exercisePath}/{ExerciseNodeType.SolutionSubNamespace}")
        {
            Name = "Solution",
            NodeType = CodeNodeType.NodeType,
            Content = new CodeConfiguration { Code = solutionCode }
        }).Should().Within(30.Seconds()).Emit();

        return new SeededExercise(
            coursePath, modulePath, exercisePath, starterPath, validationPath, solutionPath);
    }
}
