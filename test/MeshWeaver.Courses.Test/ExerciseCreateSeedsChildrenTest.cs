using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// The Exercise GUI-create pipeline
/// (<see cref="ExerciseNodeType.BuildCreateExercise"/> — the NodeType's
/// <c>BuildCreate</c>): one create seeds the Exercise node PLUS its three Code
/// stubs (executable starter, validation tests, reference solution) and
/// redirects to the new exercise.
/// </summary>
public class ExerciseCreateSeedsChildrenTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task BuildCreate_SeedsExerciseWithCodeStubs()
    {
        // A module namespace to create into — the "+" context.
        var seeded = await SeedExercise(
            starterCode: "var x = 1;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");");
        var client = GetClient();

        var control = await ExerciseNodeType.BuildCreateExercise(client, seeded.ModulePath)
            .Should().Within(60.Seconds()).Emit();

        // The pipeline redirects into the freshly created exercise, which lives
        // under the module's Exercise sub-namespace.
        var redirect = control.Should().BeOfType<RedirectControl>().Subject;
        var exercisePath = redirect.Href.ToString()!.TrimStart('/');
        exercisePath.Should().StartWith(
            $"{seeded.ModulePath}/{ExerciseNodeType.ExerciseSubNamespace}/");

        var workspace = client.GetWorkspace();
        var exercise = await workspace.GetMeshNodeStream(exercisePath)
            .Where(n => n?.Content is ExerciseConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        exercise!.NodeType.Should().Be(ExerciseNodeType.NodeType);
        ((ExerciseConfiguration)exercise.Content!).Statement.Should().NotBeNullOrEmpty();

        // The three Code stubs.
        var starter = await workspace.GetMeshNodeStream(
                $"{exercisePath}/{ExerciseNodeType.SourceSubNamespace}/{ExerciseNodeType.StarterNodeId}")
            .Where(n => n?.Content is CodeConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        ((CodeConfiguration)starter!.Content!).IsExecutable.Should().BeTrue(
            "the trainee's starting point must run in the notebook cell");

        var validation = await workspace.GetMeshNodeStream(
                $"{exercisePath}/{ExerciseNodeType.TestSubNamespace}/{ExerciseNodeType.ValidationNodeId}")
            .Where(n => n?.Content is CodeConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        validation!.NodeType.Should().Be(CodeNodeType.NodeType);

        var solution = await workspace.GetMeshNodeStream(
                $"{exercisePath}/{ExerciseNodeType.SolutionSubNamespace}/{ExerciseNodeType.SolutionNodeId}")
            .Where(n => n?.Content is CodeConfiguration)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        solution!.NodeType.Should().Be(CodeNodeType.NodeType);
    }
}
