using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// The solution reveal is a plain state flag on the attempt
/// (<see cref="ExerciseAttemptStatus.RevealedSolution"/>) flipped via
/// <c>stream.Update</c> — the workspace area reacting to it is a later (UI)
/// task; here we pin that the flag round-trips on the attempt's node stream
/// and does not disturb the rest of the attempt state.
/// </summary>
public class SolutionRevealStateTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task RevealedSolution_RoundTripsOnStream()
    {
        var seeded = await SeedExercise(
            starterCode: "var x = 1;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");",
            solutionCode: "var x = 42;");

        var client = GetClient();
        var attemptPath = await ExerciseAttemptNodeType.StartAttempt(client, seeded.ExercisePath)
            .Should().Within(60.Seconds()).Emit();

        var workspace = client.GetWorkspace();

        // Sanity: reveal starts false.
        var initial = await workspace.GetMeshNodeStream(attemptPath)
            .Select(n => n?.Content as ExerciseAttemptStatus)
            .Where(s => s is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        initial!.RevealedSolution.Should().BeFalse();

        // Flip the flag — the ONLY mutation surface.
        await workspace.GetMeshNodeStream(attemptPath)
            .Update(curr => curr.Content is ExerciseAttemptStatus status
                ? curr with { Content = status with { RevealedSolution = true } }
                : curr)
            .Should().Within(30.Seconds()).Emit();

        // The flag round-trips on the stream; the rest of the attempt state is
        // untouched (the merge-patch only carries the changed field).
        var revealed = await workspace.GetMeshNodeStream(attemptPath)
            .Select(n => n?.Content as ExerciseAttemptStatus)
            .Where(s => s is not null && s.RevealedSolution)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        revealed!.Status.Should().Be(AttemptStatus.InProgress,
            "revealing the solution must not change the attempt's lifecycle state");
        revealed.ExercisePath.Should().Be(seeded.ExercisePath);
    }
}
