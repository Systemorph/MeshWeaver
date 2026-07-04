using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// The course overview's progress section end-to-end: fork an exercise, run a
/// PASSING validation through the control plane, then render the course's
/// default <see cref="CourseLayoutAreas.ContentArea"/> and assert the viewer's
/// progress — the bar reads 1 of 1 passed and the per-module grid is present.
/// Match predicates tolerate query-index lag (the progress combines two synced
/// queries that converge after the pass is persisted).
/// </summary>
public class CourseProgressTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task ProgressShowsPassedExercise_AfterValidationPass()
    {
        // Starter already satisfies the validation — the run passes.
        var seeded = await SeedExercise(
            starterCode: "var x = 42;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");");
        var client = GetClient();
        var attemptPath = await ExerciseAttemptNodeType.StartAttempt(client, seeded.ExercisePath)
            .Should().Within(60.Seconds()).Emit();

        var workspace = client.GetWorkspace();
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

        // Wait for the pass on the attempt node (the source of truth).
        var terminal = await workspace.GetMeshNodeStream(attemptPath)
            .Select(n => n?.Content as ExerciseAttemptStatus)
            .Where(s => s is not null
                && s.Status is AttemptStatus.Passed or AttemptStatus.Failed)
            .FirstAsync().Timeout(60.Seconds()).ToTask();
        terminal!.Status.Should().Be(AttemptStatus.Passed);

        // Render the course overview: the progress bar converges on 1/1 passed
        // once the synced exercise + attempt queries catch up.
        var (stream, reference) = OpenArea(seeded.CoursePath, CourseLayoutAreas.ContentArea);
        await stream.GetControlStream(
                $"{reference.Area}/{CourseLayoutAreas.ProgressArea}/{CourseLayoutAreas.ProgressBarArea}")
            .Should().Within(90.Seconds()).Match(c =>
                c is ProgressControl progress
                && Equals(progress.Message!.ToString(), "1 of 1 exercises passed"));

        // The per-module grid rides along.
        await stream.GetControlStream(
                $"{reference.Area}/{CourseLayoutAreas.ProgressArea}/{CourseLayoutAreas.ProgressGridArea}")
            .Should().Within(30.Seconds()).Match(c => c is DataGridControl);
    }
}
