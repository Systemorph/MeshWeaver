using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// Renders the exercise's default <see cref="ExerciseLayoutAreas.WorkspaceArea"/>
/// through both lifecycle states: before any attempt (statement + Start
/// button) and after <see cref="ExerciseAttemptNodeType.StartAttempt"/> (the
/// Splitter workspace embedding the attempt working copy — Monaco edit area on
/// the left, notebook cell + Validate on the right). The state flip is driven
/// by the synced attempt query, so the Match predicates tolerate index lag.
/// </summary>
public class ExerciseWorkspaceTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task Workspace_ShowsStart_ThenAttemptWorkspaceAfterFork()
    {
        var seeded = await SeedExercise(
            starterCode: "var x = 1;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");");
        var client = GetClient();

        var (stream, reference) = OpenArea(seeded.ExercisePath, ExerciseLayoutAreas.WorkspaceArea);

        // Phase 1 — no attempt: the statement + the Start button.
        await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds()).Match(c => c is StackControl);
        await stream.GetControlStream(
                $"{reference.Area}/{ExerciseLayoutAreas.StartButtonArea}")
            .Should().Within(30.Seconds()).Match(c => c is ButtonControl);
        await stream.GetControlStream(
                $"{reference.Area}/{ExerciseLayoutAreas.StatementArea}")
            .Should().Within(30.Seconds()).Match(c =>
                c is MarkdownControl md && md.Markdown!.ToString()!.Contains("Make x equal 42."));

        // Phase 2 — fork (what the Start button's click action runs).
        var attemptPath = await ExerciseAttemptNodeType.StartAttempt(client, seeded.ExercisePath)
            .Should().Within(60.Seconds()).Emit();
        var attemptCodePath =
            $"{attemptPath}/{ExerciseNodeType.SourceSubNamespace}/{ExerciseAttemptNodeType.AttemptCodeNodeId}";

        // The area re-renders into the Splitter workspace once the synced
        // attempt query observes the new node.
        var splitter = (SplitterControl)(await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds()).Match(c =>
                c is SplitterControl s && s.Areas.Count == 2))!;
        var leftArea = splitter.Areas.First().Area!.ToString()!;
        var rightArea = splitter.Areas.Last().Area!.ToString()!;

        // Left: statement + the working copy's Monaco edit embed.
        await stream.GetControlStream($"{leftArea}/{ExerciseLayoutAreas.EditorArea}")
            .Should().Within(30.Seconds()).Match(c =>
                c is LayoutAreaControl embed
                && embed.Address.ToString()!.Contains(attemptCodePath)
                && Equals(embed.Reference.Area, CodeLayoutAreas.EditArea));

        // Right: the working copy's notebook cell (default area — Run/Cancel
        // live inside the cell, never duplicated in the workspace).
        await stream.GetControlStream($"{rightArea}/{ExerciseLayoutAreas.AttemptCellArea}")
            .Should().Within(30.Seconds()).Match(c =>
                c is LayoutAreaControl embed
                && embed.Address.ToString()!.Contains(attemptCodePath)
                && string.IsNullOrEmpty(embed.Reference.Area));

        // Right: the Validate toolbar with its button, and the status badge.
        await stream.GetControlStream($"{rightArea}/{ExerciseLayoutAreas.ValidateButtonArea}")
            .Should().Within(30.Seconds()).Match(c => c is StackControl);
        await stream.GetControlStream($"{rightArea}/{ExerciseLayoutAreas.StatusBadgeArea}")
            .Should().Within(30.Seconds()).Match(c =>
                c is LabelControl label && label.Data!.ToString()!.Contains("In progress"));
    }
}
