using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Courses.Configuration;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// The left-hand index on a course page is the WHOLE course, not the branch the reader happens to
/// stand in (#770). A reader in a lesson of module 1 must see module 2's lessons too — that is the
/// thing "the current node's children" can never produce — with their position MARKED rather than
/// the list pruned to it.
///
/// <para>Pinned twice: on <see cref="CourseNavigationProvider.BuildNavigation"/> (pure — structure,
/// ordering, exclusions, and that the index does not change as you go deeper), and end-to-end
/// through the real Overview area as the browser receives it.</para>
/// </summary>
public class CourseIndexNavigationTest(ITestOutputHelper output) : CoursesTestBase(output)
{
    private const string CourseId = "Algebra";
    private const string CoursePath = $"{CoursePartition}/{CourseId}";
    private const string ModuleOne = $"{CoursePath}/M1";
    private const string ModuleTwo = $"{CoursePath}/M2";
    private const string DeepLesson = $"{ModuleOne}/{ModuleNodeType.TheorySubNamespace}/T2";

    private static MeshNode Course => new(CourseId, CoursePartition)
    {
        Name = "Algebra", NodeType = CourseNodeType.NodeType
    };

    /// <summary>
    /// A course whose modules are declared OUT of Order order, with lessons in two different
    /// modules, an exercise with its own internal Source node, and satellites at two levels.
    /// </summary>
    private static IReadOnlyList<MeshNode> CourseSubtree() =>
    [
        // Modules declared 2-then-1 so the assertion pins Order, not declaration order.
        new("M2", CoursePath) { Name = "Second module", NodeType = ModuleNodeType.NodeType, Order = 2 },
        new("M1", CoursePath) { Name = "First module", NodeType = ModuleNodeType.NodeType, Order = 1 },

        new("T2", $"{ModuleOne}/{ModuleNodeType.TheorySubNamespace}")
            { Name = "Lesson two", NodeType = MarkdownNodeType.NodeType, Order = 2 },
        new("T1", $"{ModuleOne}/{ModuleNodeType.TheorySubNamespace}")
            { Name = "Lesson one", NodeType = MarkdownNodeType.NodeType, Order = 1 },
        new("E1", $"{ModuleOne}/{ExerciseNodeType.ExerciseSubNamespace}")
            { Name = "Drill", NodeType = ExerciseNodeType.NodeType, Order = 1 },
        // Exercise internals — rendered INSIDE the exercise, never a line of the course index.
        new(ExerciseNodeType.StarterNodeId,
            $"{ModuleOne}/{ExerciseNodeType.ExerciseSubNamespace}/E1/{ExerciseNodeType.SourceSubNamespace}")
            { Name = "Starter", NodeType = CodeNodeType.NodeType },

        new("T3", $"{ModuleTwo}/{ModuleNodeType.TheorySubNamespace}")
            { Name = "Lesson three", NodeType = MarkdownNodeType.NodeType, Order = 1 },

        // Satellites at course level and under a lesson — plumbing, never learner-facing pages.
        new("c1", $"{CoursePath}/_Comment") { Name = "A comment" },
        new("a1", $"{ModuleOne}/{ModuleNodeType.TheorySubNamespace}/T1/_Access") { Name = "An assignment" },
    ];

    private static IEnumerable<string> PathsOf(NodeNavigation nav)
        => nav.Entries.SelectMany(e => new[] { e.Path }.Concat(e.Children.Select(c => c.Path)));

    [Fact]
    public void FromADeepLesson_TheIndexListsTheWholeCourse_GroupedByModule_OrderedByOrder()
    {
        var nav = CourseNavigationProvider.BuildNavigation(Course, CourseSubtree(), DeepLesson);

        nav.Title.Should().Be("Algebra", "the heading names what is indexed — the course, not the lesson");
        nav.TitlePath.Should().Be(CoursePath);

        // Grouped by module, ordered by Order (M1 declared second, listed first).
        nav.Entries.Select(e => e.Path).Should().Equal(ModuleOne, ModuleTwo);
        nav.Entries.Select(e => e.Label).Should().Equal("First module", "Second module");

        // Module 1: theory before exercises, each ordered by Order.
        nav.Entries[0].Children.Select(c => c.Path).Should().Equal(
            $"{ModuleOne}/{ModuleNodeType.TheorySubNamespace}/T1",
            DeepLesson,
            $"{ModuleOne}/{ExerciseNodeType.ExerciseSubNamespace}/E1");

        // THE point of #770: the OTHER module's lesson is listed too. Children-of-the-current-node
        // could never surface this — the reader is three levels away from it.
        nav.Entries[1].Children.Select(c => c.Path).Should().Equal(
            $"{ModuleTwo}/{ModuleNodeType.TheorySubNamespace}/T3");
    }

    [Fact]
    public void TheCurrentLessonIsTheOnlyMarkedEntry()
    {
        var nav = CourseNavigationProvider.BuildNavigation(Course, CourseSubtree(), DeepLesson);

        var marked = nav.Entries
            .SelectMany(e => new[] { e }.Concat(e.Children))
            .Where(e => e.IsCurrent)
            .ToList();

        marked.Should().ContainSingle("exactly one entry can be where the reader stands")
            .Which.Path.Should().Be(DeepLesson);
    }

    [Fact]
    public void APageBelowTheIndexMarksTheDeepestEntryThatContainsIt()
    {
        // Reading an exercise's starter code: the starter is not a line of the index, so the
        // EXERCISE carries the marker. A reader always has a position, at any depth.
        var inside = $"{ModuleOne}/{ExerciseNodeType.ExerciseSubNamespace}/E1/"
                     + $"{ExerciseNodeType.SourceSubNamespace}/{ExerciseNodeType.StarterNodeId}";
        var nav = CourseNavigationProvider.BuildNavigation(Course, CourseSubtree(), inside);

        nav.Entries.SelectMany(e => e.Children).Single(c => c.IsCurrent)
            .Path.Should().Be($"{ModuleOne}/{ExerciseNodeType.ExerciseSubNamespace}/E1");
    }

    [Fact]
    public void SatellitesAndExerciseInternalsAreExcluded()
    {
        var paths = PathsOf(CourseNavigationProvider.BuildNavigation(Course, CourseSubtree(), DeepLesson))
            .ToList();

        paths.Should().NotContain(p => p.Contains("/_"), "_-prefixed satellites are plumbing, not pages");
        paths.Should().NotContain(p => p.Contains($"/{ExerciseNodeType.SourceSubNamespace}/"),
            "an exercise's own code nodes render inside the exercise, not in the course index");
    }

    [Fact]
    public void TheIndexIsIdenticalAtEveryDepth_OnlyTheMarkerMoves()
    {
        var subtree = CourseSubtree();
        var atRoot = CourseNavigationProvider.BuildNavigation(Course, subtree, CoursePath);

        foreach (var depth in new[] { ModuleOne, DeepLesson, $"{ModuleTwo}/{ModuleNodeType.TheorySubNamespace}/T3" })
        {
            var here = CourseNavigationProvider.BuildNavigation(Course, subtree, depth);
            PathsOf(here).Should().Equal(PathsOf(atRoot),
                $"navigating to '{depth}' must move the marker, never re-scope or shrink the index");
        }
    }

    [Fact]
    public void APageOutsideAnyCourseGetsNoSuppliedIndex()
        => CourseNavigationProvider
            .EnclosingCourse([Course], $"{CoursePartition}/SomethingElse/Page")
            .Should().BeNull("core's default child list must stand for docs and spaces");

    [Fact(Timeout = 120_000)]
    public async Task Overview_OnADeepLesson_RendersTheWholeCourseIndex()
    {
        await SeedCourse();

        var (stream, reference) = OpenArea(DeepLesson, MarkdownLayoutAreas.OverviewArea);

        // The page is the nav column + the content column; the nav is addressable by its area id.
        var menu = (NavMenuControl)(await stream
            .GetControlStream($"{reference.Area}/{MarkdownOverviewLayoutArea.NavigationArea}")
            .Should().Within(60.Seconds()).Match(c => c is NavMenuControl { Areas.Count: 1 }))!;

        // The heading is the COURSE, and it holds one group per module — both of them, from a lesson
        // three levels down. Live index: wait for the emission that has the whole course.
        var courseGroup = (NavGroupControl)(await stream
            .GetControlStream(menu.Areas[0].Area!.ToString()!)
            .Should().Within(60.Seconds()).Match(c => c is NavGroupControl { Areas.Count: 2 }))!;
        courseGroup.Title.ToString().Should().Be("Algebra");

        // Module 1 — the reader's own module: two theory lessons then the exercise. The current
        // lesson is MARKED and NOT a link; its sibling still is.
        var currentModule = (NavGroupControl)(await stream
            .GetControlStream(courseGroup.Areas[0].Area!.ToString()!)
            .Should().Within(60.Seconds()).Match(c => c is NavGroupControl { Areas.Count: 3 }))!;

        var here = await stream.GetControlStream(currentModule.Areas[1].Area!.ToString()!)
            .Should().Within(60.Seconds()).Match(c => c is LabelControl);
        here.Should().BeOfType<LabelControl>(
                "a link to the page you are on is a dead control — the current entry is marked text")
            .Which.Data.ToString().Should()
            .StartWith(MarkdownOverviewLayoutArea.CurrentMarker,
                "the marker is a glyph as well as an accent, so it survives for a reader who cannot see the colour");

        var sibling = await stream.GetControlStream(currentModule.Areas[0].Area!.ToString()!)
            .Should().Within(60.Seconds()).Match(c => c is NavLinkControl);
        sibling.Should().BeOfType<NavLinkControl>().Which.Url?.ToString().Should()
            .Be($"/{ModuleOne}/{ModuleNodeType.TheorySubNamespace}/T1");

        // Module 2 — a module the reader is NOT in still lists its lesson. This is the whole point:
        // the index is the course, not the branch.
        var otherModule = (NavGroupControl)(await stream
            .GetControlStream(courseGroup.Areas[1].Area!.ToString()!)
            .Should().Within(60.Seconds()).Match(c => c is NavGroupControl { Areas.Count: 1 }))!;

        var otherLesson = await stream.GetControlStream(otherModule.Areas[0].Area!.ToString()!)
            .Should().Within(60.Seconds()).Match(c => c is NavLinkControl);
        otherLesson.Should().BeOfType<NavLinkControl>().Which.Url?.ToString().Should()
            .Be($"/{ModuleTwo}/{ModuleNodeType.TheorySubNamespace}/T3");
    }

    /// <summary>
    /// Seeds the course of <see cref="CourseSubtree"/> into the mesh: two modules with one theory
    /// lesson each, plus a second lesson in module 1 (the page the render test opens).
    /// </summary>
    private async Task SeedCourse()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(Course).Should().Within(30.Seconds()).Emit();
        foreach (var node in CourseSubtree().Where(n => !n.Path.Contains("/_")))
            await mesh.CreateNode(node).Should().Within(30.Seconds()).Emit();
    }
}
