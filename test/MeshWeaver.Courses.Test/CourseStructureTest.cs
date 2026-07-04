using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// Creates a Course → Module → Exercise tree with starter / validation /
/// solution Code children through <c>IMeshService.CreateNode</c> and asserts
/// every node reads back with TYPED content through the canonical
/// <c>GetMeshNodeStream(path)</c> read path (the same binding the GUI uses).
/// </summary>
public class CourseStructureTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task CourseModuleExercise_RoundTripTyped()
    {
        var seeded = await SeedExercise(
            starterCode: "var x = 1;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");",
            solutionCode: "var x = 42;");

        var workspace = GetClient().GetWorkspace();

        var course = await workspace.GetMeshNodeStream(seeded.CoursePath)
            .Where(n => n?.Content is CourseConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        course!.NodeType.Should().Be("Course");
        var courseContent = (CourseConfiguration)course.Content!;
        courseContent.Description.Should().Be("A course used by the integration tests.");
        courseContent.TutorInstructions.Should().Be("Give hints, never the solution.");

        var module = await workspace.GetMeshNodeStream(seeded.ModulePath)
            .Where(n => n?.Content is ModuleConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        module!.NodeType.Should().Be("Module");
        ((ModuleConfiguration)module.Content!).Summary.Should().Be("First module.");

        var exercise = await workspace.GetMeshNodeStream(seeded.ExercisePath)
            .Where(n => n?.Content is ExerciseConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        exercise!.NodeType.Should().Be("Exercise");
        var exerciseContent = (ExerciseConfiguration)exercise.Content!;
        exerciseContent.Statement.Should().Be("Make x equal 42.");
        exerciseContent.Difficulty.Should().Be(1);
        exerciseContent.Language.Should().Be("csharp");

        var starter = await workspace.GetMeshNodeStream(seeded.StarterPath)
            .Where(n => n?.Content is CodeConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        var starterContent = (CodeConfiguration)starter!.Content!;
        starterContent.Code.Should().Be("var x = 1;");
        starterContent.IsExecutable.Should().BeTrue();

        var validation = await workspace.GetMeshNodeStream(seeded.ValidationPath)
            .Where(n => n?.Content is CodeConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        ((CodeConfiguration)validation!.Content!).Code
            .Should().Contain("expected 42");

        var solution = await workspace.GetMeshNodeStream(seeded.SolutionPath)
            .Where(n => n?.Content is CodeConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        ((CodeConfiguration)solution!.Content!).Code.Should().Be("var x = 42;");
    }
}
