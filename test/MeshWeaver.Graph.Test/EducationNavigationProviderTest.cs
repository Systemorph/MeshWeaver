using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The left-hand index on a NODE-NATIVE course page (an <c>Edu/Lesson</c>-shaped plugin partition)
/// is the WHOLE course, not the branch the reader stands in. This is the plugin-course twin of
/// <c>CourseIndexNavigationTest</c>: the 2026-08-08 fix repaired the one-branch menu for core's own
/// Courses types only, and every GitSync'd course (ThinkInStreams, Foundations, the primers) kept
/// the one-lesson menu — which is what kept being reported. Pinned on the pure
/// <see cref="EducationNavigationProvider.BuildNavigation"/>: structure, ordering, exclusions,
/// position marking, and that the index does not change as the reader goes deeper.
/// </summary>
public class EducationNavigationProviderTest
{
    private const string Course = "ThinkInStreams";
    private const string L1 = $"{Course}/01-Trap";
    private const string L2 = $"{Course}/02-Specify";
    private const string L3 = $"{Course}/03-Combine";

    /// <summary>
    /// A ThinkInStreams-shaped course: lessons declared OUT of Order order, each with an
    /// Exercise/ folder, a Solution subtree, page-embedded Source code, and a quiz; course-level
    /// pages beside them; satellites and the sync config sprinkled in.
    /// </summary>
    private static IReadOnlyList<MeshNode> CourseSubtree() =>
    [
        new("ThinkInStreams", "") { Name = "Think in Streams", NodeType = "Store/Plugin" },

        // Lessons declared 2-1-3 so the assertions pin Order, not declaration order.
        new("02-Specify", Course) { Name = "Lesson 2", NodeType = "Edu/Lesson", Order = 2 },
        new("01-Trap", Course) { Name = "Lesson 1", NodeType = "Edu/Lesson", Order = 1 },
        new("03-Combine", Course) { Name = "Lesson 3", NodeType = "Edu/Lesson", Order = 3 },

        // Lesson 1 internals: exercises live in a folder, code lives in Source (page-embedded).
        new("Exercise", L1) { Name = "Exercises", NodeType = "Markdown" },
        new("E2", $"{L1}/Exercise") { Name = "Exercise two", NodeType = "Edu/Exercise", Order = 2 },
        new("E1", $"{L1}/Exercise") { Name = "Exercise one", NodeType = "Edu/Exercise", Order = 1 },
        new("Solution", L1) { Name = "Solutions", NodeType = "Markdown", Order = 90 },
        new("S1", $"{L1}/Solution") { Name = "Solution one", NodeType = "Markdown" },
        new("Source", L1) { Name = "Source", NodeType = "Markdown" },
        new("Demo1", $"{L1}/Source") { Name = "A demo cell", NodeType = "Code" },
        new("Quiz", L1) { Name = "Lesson 1 Quiz", NodeType = "Edu/Quiz", Order = 80 },

        // Course-level pages and plumbing.
        new("Exercises", Course) { Name = "All exercises", NodeType = "Markdown", Order = 95 },
        new("_GitSync", Course) { Name = "GitHub Sync", NodeType = "GitHubSyncConfig" },
        new("p1", $"{Course}/_Policy") { Name = "Access policy" },
    ];

    private static IReadOnlyList<string> AllPaths(NodeNavigation nav)
        => nav.Entries.SelectMany(e => new[] { e.Path }.Concat(e.Children.Select(c => c.Path))).ToList();

    [Fact]
    public void FromADeepPage_TheIndexListsEveryLesson_OrderedByOrder()
    {
        var nav = EducationNavigationProvider.BuildNavigation(
            Course, CourseSubtree(), $"{L1}/Solution/S1");

        nav.Should().NotBeNull();
        nav!.Title.Should().Be("Think in Streams", "the heading names the course, not the page");
        nav.TitlePath.Should().Be(Course);

        // EVERY lesson is an entry, ordered by Order (Lesson 2 declared first, listed second).
        nav.Entries.Select(e => e.Path).Should().ContainInOrder(L1, L2, L3);
        nav.Entries.Select(e => e.Path).Should().Contain($"{Course}/Exercises");
    }

    [Fact]
    public void LessonInternals_SourceStaysOut_ExercisesFlattenToTheEnd()
    {
        var nav = EducationNavigationProvider.BuildNavigation(Course, CourseSubtree(), L1)!;

        var lesson = nav.Entries.Single(e => e.Path == L1);
        var childPaths = lesson.Children.Select(c => c.Path).ToList();

        childPaths.Should().NotContain($"{L1}/Source",
            "Source code renders INSIDE the lesson page — listing it buries the course structure");
        childPaths.Should().NotContain($"{L1}/Exercise",
            "the Exercise folder is replaced by the exercises it holds");
        // Reading pages first (quiz Order 80 before solutions Order 90), exercises LAST.
        childPaths.Should().ContainInOrder(
            $"{L1}/Quiz", $"{L1}/Solution", $"{L1}/Exercise/E1", $"{L1}/Exercise/E2");
    }

    [Fact]
    public void TheIndexIsTheSame_WhereverTheReaderStands_OnlyTheMarkerMoves()
    {
        var fromLesson1 = EducationNavigationProvider.BuildNavigation(Course, CourseSubtree(), L1)!;
        var fromLesson3 = EducationNavigationProvider.BuildNavigation(Course, CourseSubtree(), L3)!;

        AllPaths(fromLesson1).Should().Equal(AllPaths(fromLesson3),
            "navigating moves the position marker — it never re-scopes or shrinks the index");

        fromLesson1.Entries.Single(e => e.IsCurrent).Path.Should().Be(L1);
        fromLesson3.Entries.Single(e => e.IsCurrent).Path.Should().Be(L3);
    }

    [Fact]
    public void ADeepUnlistedPage_MarksTheDeepestListedAncestor()
    {
        // {L1}/Solution/S1 is below what the index lists → its Solution parent carries the marker.
        var nav = EducationNavigationProvider.BuildNavigation(
            Course, CourseSubtree(), $"{L1}/Solution/S1")!;

        var lesson = nav.Entries.Single(e => e.Path == L1);
        lesson.Children.Single(c => c.IsCurrent).Path.Should().Be($"{L1}/Solution");
        nav.Entries.Count(e => e.IsCurrent).Should().Be(0,
            "the deeper Solution entry carries the position, not the lesson group too");
    }

    [Fact]
    public void ALearnersOwnCopy_IndexesTheCopy()
    {
        // The same course installed under the viewer's home: same shape, viewer-prefixed paths.
        var copy = CourseSubtree()
            .Select(n => new MeshNode(n.Id, string.IsNullOrEmpty(n.Namespace) ? "learner" : $"learner/{n.Namespace}")
            {
                Name = n.Name, NodeType = n.NodeType, Order = n.Order,
            })
            .ToList();

        var nav = EducationNavigationProvider.BuildNavigation(
            $"learner/{Course}", copy, $"learner/{L2}")!;

        nav.TitlePath.Should().Be($"learner/{Course}",
            "the learner's copy is where they read and edit — its menu navigates the copy");
        nav.Entries.Select(e => e.Path).Should().ContainInOrder(
            $"learner/{L1}", $"learner/{L2}", $"learner/{L3}");
        nav.Entries.Single(e => e.IsCurrent).Path.Should().Be($"learner/{L2}");
    }

    [Fact]
    public void APlainDocsSpace_IsDeclined()
    {
        IReadOnlyList<MeshNode> docs =
        [
            new("Doc", "") { Name = "Docs", NodeType = "Space" },
            new("Page1", "Doc") { Name = "A page", NodeType = "Markdown" },
            new("Page2", "Doc") { Name = "Another", NodeType = "Markdown" },
        ];

        EducationNavigationProvider.BuildNavigation("Doc", docs, "Doc/Page1")
            .Should().BeNull("no education-typed node → core's default child list stands");
    }

    [Fact]
    public void CoreCoursesTypes_AreNotClaimed()
    {
        // Core's own Course/Module types (no module prefix) belong to MeshWeaver.Courses'
        // provider — claiming them here would race two menus for the same page.
        IReadOnlyList<MeshNode> core =
        [
            new("Algebra", "") { Name = "Algebra", NodeType = "Course" },
            new("M1", "Algebra") { Name = "Module", NodeType = "Module" },
        ];

        EducationNavigationProvider.LooksLikeCourse(core).Should().BeFalse();
    }

    // ── Progress: captured in the learner's home, decorated back into the menu ──

    [Fact]
    public void LessonSlug_IsTheSegmentUnderTheRoot_InBothTrees()
    {
        CourseProgress.LessonSlug(Course, $"{L1}/Solution/S1").Should().Be("01-Trap");
        CourseProgress.LessonSlug(Course, L2).Should().Be("02-Specify");
        CourseProgress.LessonSlug($"learner/{Course}", $"learner/{L1}/Exercise/E1")
            .Should().Be("01-Trap", "the copy and the central course share lesson keys");
        (CourseProgress.LessonSlug(Course, Course) is null)
            .Should().BeTrue("the course home is not a lesson");
        (CourseProgress.LessonSlug(Course, $"{Course}/_Policy/p1") is null)
            .Should().BeTrue("a satellite is never a lesson");
    }

    [Fact]
    public void VisitMarker_IsSelfDescribing_AndKeyedForIdempotentUpsert()
    {
        var marker = CourseProgress.VisitMarker(
            "learner", "ThinkInStreams", "01-Trap", $"{L1}/Quiz", "2026-08-12T10:00:00Z");

        marker.Path.Should().Be("learner/_Progress/ThinkInStreams/Visited/01-Trap",
            "one marker per lesson — a re-visit upserts the SAME node, no read-modify-write");
        marker.NodeType.Should().Be("Markdown");

        // The resume record (Edu #428) lives at learner/_Progress/ThinkInStreams — the marker is
        // its CHILD, so the two writers can never clobber each other.
        marker.Path.Should().StartWith("learner/_Progress/ThinkInStreams/");
    }

    [Fact]
    public void VisitedSlugs_ReadsTheMarkerIds()
    {
        var markers = new[]
        {
            CourseProgress.VisitMarker("learner", "ThinkInStreams", "01-Trap", L1, "2026-08-12T10:00:00Z"),
            CourseProgress.VisitMarker("learner", "ThinkInStreams", "03-Combine", L3, "2026-08-12T11:00:00Z"),
        };

        CourseProgress.VisitedSlugs(markers).OrderBy(x => x).Should().Equal("01-Trap", "03-Combine");
    }

    [Fact]
    public void DecorateVisited_MarksVisitedLessons_InBothTrees()
    {
        var nav = EducationNavigationProvider.BuildNavigation(Course, CourseSubtree(), L2)!;
        var visited = new HashSet<string>(System.StringComparer.Ordinal) { "01-Trap" };

        var decorated = EducationNavigationProvider.DecorateVisited(nav, visited)!;
        decorated.Entries.Single(e => e.Path == L1).Label.Should().StartWith("✓ ");
        decorated.Entries.Single(e => e.Path == L2).Label.Should().NotStartWith("✓ ");

        // The learner's own copy decorates identically: lesson KEYS are folder names, shared by
        // both trees.
        var copyNav = nav with
        {
            Entries = nav.Entries.Select(e => e with { Path = $"learner/{e.Path}" }).ToList(),
        };
        EducationNavigationProvider.DecorateVisited(copyNav, visited)!
            .Entries.Single(e => e.Path == $"learner/{L1}").Label.Should().StartWith("✓ ");
    }
}
