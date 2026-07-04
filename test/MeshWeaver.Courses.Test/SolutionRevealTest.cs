using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// The solution-reveal flow at the RENDER level: the workspace initially hides
/// the reference solution; flipping
/// <see cref="ExerciseAttemptStatus.RevealedSolution"/> on the attempt node via
/// <c>GetMeshNodeStream(attemptPath).Update(...)</c> (exactly what the Reveal
/// button's click action does) re-renders the workspace with the solution
/// embed.
/// </summary>
public class SolutionRevealTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task RevealFlag_MakesSolutionEmbedAppear()
    {
        var seeded = await SeedExercise(
            starterCode: "var x = 1;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");",
            solutionCode: "var x = 42;");
        var client = GetClient();
        var attemptPath = await ExerciseAttemptNodeType.StartAttempt(client, seeded.ExercisePath)
            .Should().Within(60.Seconds()).Emit();

        var (stream, reference) = OpenArea(seeded.ExercisePath, ExerciseLayoutAreas.WorkspaceArea);

        // The attempt workspace, solution CONCEALED: the right pane offers the
        // reveal toggle but carries no solution embed.
        var splitter = (SplitterControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds()).Match(c =>
                c is SplitterControl s && s.Areas.Count == 2))!;
        var rightArea = splitter.Areas.Last().Area!.ToString()!;

        var rightPane = (StackControl)(await stream.GetControlStream(rightArea)
            .Should().Within(30.Seconds()).Match(c => c is StackControl))!;
        rightPane.Areas.Should().Contain(a =>
            a.Area!.ToString()!.EndsWith("/" + ExerciseLayoutAreas.RevealSolutionButtonArea));
        rightPane.Areas.Should().NotContain(a =>
            a.Area!.ToString()!.EndsWith("/" + ExerciseLayoutAreas.SolutionArea),
            "the solution stays concealed until the trainee reveals it");

        // Flip the reveal flag — the canonical stream.Update the button runs.
        await client.GetWorkspace().GetMeshNodeStream(attemptPath)
            .Update(curr => curr.Content is ExerciseAttemptStatus status
                ? curr with { Content = status with { RevealedSolution = true } }
                : curr)
            .Should().Within(30.Seconds()).Emit();

        // The workspace re-renders with the solution embed.
        await stream.GetControlStream($"{rightArea}/{ExerciseLayoutAreas.SolutionArea}")
            .Should().Within(60.Seconds()).Match(c =>
                c is LayoutAreaControl embed
                && embed.Address.ToString()!.Contains(seeded.SolutionPath));
    }
}
