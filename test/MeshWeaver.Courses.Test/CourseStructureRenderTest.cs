using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// Renders the module's default <see cref="ModuleLayoutAreas.ContentArea"/> and
/// asserts the page composition: theory and example children embedded via
/// <see cref="LayoutAreaControl"/> (default area) and a tab strip whose tabs
/// embed each exercise's <see cref="ExerciseLayoutAreas.WorkspaceArea"/>.
/// Match predicates tolerate query-index lag — the area re-emits as children
/// land in the synced queries.
/// </summary>
public class CourseStructureRenderTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task ModuleContent_EmbedsTheoryExamplesAndExerciseTabs()
    {
        var seeded = await SeedExercise(
            starterCode: "var x = 1;",
            validationCode: "if (x != 42) throw new System.Exception(\"expected 42\");");
        var theoryPath = await SeedTheory(seeded.ModulePath, "Intro", "# Welcome\nTheory text.");
        var examplePath = await SeedExample(seeded.ModulePath, "Demo", "var demo = 1 + 1;");

        var (stream, reference) = OpenArea(seeded.ModulePath, ModuleLayoutAreas.ContentArea);

        // Root: the module page stack.
        await stream.GetControlStream(reference.Area!)
            .Should().Within(60.Seconds()).Match(c => c is StackControl);

        // Theory embed: LayoutAreaControl → the theory node's DEFAULT area.
        await stream.GetControlStream(
                $"{reference.Area}/{ModuleLayoutAreas.TheorySection}/Embed-Intro")
            .Should().Within(60.Seconds()).Match(c =>
                c is LayoutAreaControl embed
                && embed.Address.ToString()!.Contains(theoryPath)
                && string.IsNullOrEmpty(embed.Reference.Area));

        // Example embed.
        await stream.GetControlStream(
                $"{reference.Area}/{ModuleLayoutAreas.ExampleSection}/Embed-Demo")
            .Should().Within(60.Seconds()).Match(c =>
                c is LayoutAreaControl embed
                && embed.Address.ToString()!.Contains(examplePath));

        // Exercise tabs: one tab per exercise, each embedding the workspace area.
        var tabs = (TabsControl)(await stream.GetControlStream(
                $"{reference.Area}/{ModuleLayoutAreas.ExerciseTabsArea}")
            .Should().Within(60.Seconds()).Match(c =>
                c is TabsControl t && t.Areas.Count >= 1))!;

        var firstTabArea = tabs.Areas.First().Area!.ToString()!;
        await stream.GetControlStream(firstTabArea)
            .Should().Within(60.Seconds()).Match(c =>
                c is LayoutAreaControl embed
                && embed.Address.ToString()!.Contains(seeded.ExercisePath)
                && Equals(embed.Reference.Area, ExerciseLayoutAreas.WorkspaceArea));
    }
}
