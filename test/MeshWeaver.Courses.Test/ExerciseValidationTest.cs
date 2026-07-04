using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// Exercises the validation control plane end-to-end: flipping
/// <see cref="ExerciseAttemptStatus.ValidationRequestedAt"/> via
/// <c>GetMeshNodeStream(attemptPath).Update(...)</c> (NO request message) makes
/// the per-attempt hub's watcher CAS-claim the trigger, run the trainee code
/// concatenated with the exercise's validation tests on the kernel, and stamp
/// <see cref="AttemptStatus.Passed"/> / <see cref="AttemptStatus.Failed"/>
/// (+ <see cref="ExerciseAttemptStatus.LastValidationActivityPath"/>) back on
/// the attempt.
/// </summary>
public class ExerciseValidationTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    private const string ValidationCode =
        "if (x != 42) throw new System.Exception(\"expected 42\");";

    [Fact(Timeout = 120_000)]
    public async Task ExerciseValidationPassTest()
    {
        // Starter already satisfies the spec — validation must pass.
        var final = await RunValidation(starterCode: "var x = 42;");

        final.Status.Should().Be(AttemptStatus.Passed,
            "the concatenated submission defines x = 42, so the validation assertion holds");
        final.PassedAt.Should().NotBeNull("a pass stamps PassedAt");
        final.LastValidationActivityPath.Should().NotBeNullOrEmpty(
            "the watcher records the validation activity for the output pane");
        final.LastValidationHandledAt.Should().NotBeNull(
            "the CAS claim stamps LastValidationHandledAt");
    }

    [Fact(Timeout = 120_000)]
    public async Task ExerciseValidationFailTest()
    {
        // Starter violates the spec — the validation script throws → Failed.
        var final = await RunValidation(starterCode: "var x = 1;");

        final.Status.Should().Be(AttemptStatus.Failed,
            "the validation assertion throws for x = 1, terminating the activity as Failed");
        final.PassedAt.Should().BeNull("a failed validation never stamps PassedAt");
        final.LastValidationActivityPath.Should().NotBeNullOrEmpty(
            "even a failed run records its validation activity");
    }

    /// <summary>
    /// Seeds an exercise with the given starter, forks it, requests a
    /// validation via stream.Update and awaits the attempt's terminal
    /// (Passed/Failed) state.
    /// </summary>
    private async Task<ExerciseAttemptStatus> RunValidation(string starterCode)
    {
        var seeded = await SeedExercise(starterCode, ValidationCode);
        var client = GetClient();
        var attemptPath = await ExerciseAttemptNodeType.StartAttempt(client, seeded.ExercisePath)
            .Should().Within(60.Seconds()).Emit();

        var workspace = client.GetWorkspace();

        // The canonical request: flip the trigger trio on the attempt node —
        // no request/response message anywhere.
        await workspace.GetMeshNodeStream(attemptPath)
            .Update(curr => curr.Content is ExerciseAttemptStatus status
                ? curr with
                {
                    Content = status with
                    {
                        ValidationRequestedAt = DateTimeOffset.UtcNow,
                        ValidationRequestedBy = TestUsers.Admin.ObjectId
                    }
                }
                : curr)
            .Should().Within(30.Seconds()).Emit();

        // Observe the attempt to its terminal state — the same binding the GUI
        // uses. 60 s bound covers the kernel's cold Roslyn compile on CI.
        var terminal = await workspace.GetMeshNodeStream(attemptPath)
            .Select(n => n?.Content as ExerciseAttemptStatus)
            .Where(s => s is not null
                && s.Status is AttemptStatus.Passed or AttemptStatus.Failed)
            .FirstAsync().Timeout(60.Seconds()).ToTask();
        return terminal!;
    }
}
