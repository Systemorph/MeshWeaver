using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The left-hand rail a module supplies through <see cref="INodeNavigationProvider"/> — what it
/// CONTAINS and in what ORDER, asserted on the plan rather than on a control tree whose children
/// are protected.
///
/// <para>Three defects were reported together on a live course (2026-08-19) and every one of them
/// was invisible to a control-level test: the entry the reader stood on dropped out of the tree,
/// clicking the index title collapsed the whole index, and a long index rendered as an
/// undifferentiated wall of links with no visible expanders. A fourth followed on 2026-09-06
/// (#3406): the rail sorted by file kind before course order, so an entry with children could never
/// precede one without. Each has a case here.</para>
/// </summary>
public class SuppliedNavigationRailTest
{
    private const string Course = "Course";

    private static NodeNavigation Supplied(string? titlePath = Course) =>
        new("The Course",
        [
            new NodeNavigationEntry("Lesson 1", "Course/L1", Icon: "📘")
            {
                Children =
                [
                    new NodeNavigationEntry("Lesson 1 Quiz", "Course/L1/Quiz", Icon: "❓"),
                    new NodeNavigationEntry("Lesson 1 Solutions", "Course/L1/Solution"),
                ],
            },
            new NodeNavigationEntry("Lesson 2", "Course/L2")
            {
                Children = [new NodeNavigationEntry("Lesson 2 Quiz", "Course/L2/Quiz")],
            },
            new NodeNavigationEntry("All exercises", "Course/Exercises"),
        ])
        { TitlePath = titlePath };

    /// <summary>The rail's collapsible entries, in rail order.</summary>
    private static List<SuppliedNavigationRail.RailGroup> Groups(SuppliedNavigationRail.Rail rail)
        => rail.Items.OfType<SuppliedNavigationRail.RailGroup>().ToList();

    /// <summary>The rail's flat entries, in rail order.</summary>
    private static List<SuppliedNavigationRail.RailLink> Pages(SuppliedNavigationRail.Rail rail)
        => rail.Items.OfType<SuppliedNavigationRail.RailLink>().ToList();

    /// <summary>The path of every top-level entry, in the order the rail will render them.</summary>
    private static List<string> ItemPaths(SuppliedNavigationRail.Rail rail)
        => rail.Items.Select(Path).ToList();

    private static string Path(SuppliedNavigationRail.RailItem item) => item switch
    {
        SuppliedNavigationRail.RailGroup group => group.Path,
        SuppliedNavigationRail.RailLink link => link.Path,
        _ => throw new System.NotSupportedException(item.GetType().Name),
    };

    private static IEnumerable<SuppliedNavigationRail.RailLink> Links(SuppliedNavigationRail.RailItem item)
        => item is SuppliedNavigationRail.RailGroup group
            ? group.Links
            : [(SuppliedNavigationRail.RailLink)item];

    private static IEnumerable<SuppliedNavigationRail.RailLink> AllLinks(SuppliedNavigationRail.Rail rail)
        => new[] { rail.Home }.Concat(rail.Items.SelectMany(Links));

    [Fact]
    public void TheIndexIsTheSameWhereverTheReaderStands()
    {
        var seen = new[] { Course, "Course/L1", "Course/L1/Quiz", "Course/L2", "Course/Exercises" }
            .Select(p => AllLinks(SuppliedNavigationRail.Plan(Supplied(), p))
                .Select(l => $"{l.Label}→{l.Path}")
                .ToList())
            .ToList();

        seen.Should().OnlyContain(links => links.SequenceEqual(seen[0]),
            "standing somewhere else moves the marker and opens a different group — it never "
            + "re-scopes or shrinks the index");
    }

    [Fact]
    public void TheTitleIsALinkNotTheCollapsibleRoot()
    {
        var rail = SuppliedNavigationRail.Plan(Supplied(), "Course/L1");

        rail.Home.Href.Should().Be("/Course");
        Groups(rail).Should().HaveCount(2, "the entries are TOP-LEVEL groups, siblings of the title");
        Groups(rail).Should().NotContain(g => g.Label == rail.Home.Label,
            "nothing nests the index under its own heading — that is what made clicking the title "
            + "collapse everything");
    }

    [Fact]
    public void TheCurrentEntryStaysInTheTreeAsAnActiveLink()
    {
        var rail = SuppliedNavigationRail.Plan(
            Supplied() with
            {
                Entries =
                [
                    new NodeNavigationEntry("Lesson 1", "Course/L1", Icon: "📘")
                    {
                        Children =
                        [
                            new NodeNavigationEntry("Lesson 1 Quiz", "Course/L1/Quiz", IsCurrent: true, Icon: "❓"),
                        ],
                    },
                ],
            },
            "Course/L1/Quiz");

        var current = AllLinks(rail).Should().ContainSingle(l => l.IsCurrent).Subject;
        current.Path.Should().Be("Course/L1/Quiz");
        current.Href.Should().Be("/Course/L1/Quiz", "it is still a link, not inert text");
        current.Icon.Should().Be("❓", "it keeps its icon");
        Groups(rail).Single().Links.Should().Contain(l => l.IsCurrent,
            "and it sits inside its own group, in the same slot as when it was not current");
    }

    [Fact]
    public void AnEntryWithChildrenIsAGroupAndItsOwnFirstLink()
    {
        var rail = SuppliedNavigationRail.Plan(Supplied(), "Course/L1");
        var lesson = Groups(rail).Single(g => g.Path == "Course/L1");

        lesson.Links.Select(l => l.Path).Should().Equal(
            "Course/L1", "Course/L1/Quiz", "Course/L1/Solution");
        lesson.Links[0].Href.Should().Be("/Course/L1",
            "a group HEADING has no active state to carry the position — the self-link does");
    }

    [Fact]
    public void OnlyTheGroupTheReaderIsInsideIsExpanded()
    {
        foreach (var inside in new[] { "Course/L1", "Course/L1/Quiz", "Course/L1/Solution/Deep" })
            Groups(SuppliedNavigationRail.Plan(Supplied(), inside))
                .Where(g => g.Expanded).Select(g => g.Path)
                .Should().Equal(["Course/L1"], "from {0}", inside);

        Groups(SuppliedNavigationRail.Plan(Supplied(), Course))
            .Should().OnlyContain(g => !g.Expanded,
                "on the index root every group is collapsed — which is what makes its expander visible");
    }

    [Fact]
    public void AChildlessEntryIsAFlatLink()
    {
        var rail = SuppliedNavigationRail.Plan(Supplied(), Course);

        Pages(rail).Select(p => p.Path).Should().Equal("Course/Exercises");
        rail.Home.IsCurrent.Should().BeTrue("the reader is standing on the index root");
        AllLinks(rail).Count(l => l.IsCurrent).Should().Be(1, "exactly one line is ever current");
    }

    [Fact]
    public void AMissingRootNodeLeavesTheHeadingUnlinked()
    {
        var rail = SuppliedNavigationRail.Plan(Supplied(titlePath: null), "Course/L1");

        rail.Home.Href.Should().BeNull(
            "linking a root node that is not there renders 'no renderer is registered for area {Root}'");
        rail.Home.Label.Should().Be("The Course", "the heading still names what is indexed");
        Groups(rail).Should().HaveCount(2, "the index itself is unaffected — only the dead link is withheld");
    }

    [Fact]
    public void TheHeadingAndTheGroupsKeepTheirIcons()
    {
        var rail = SuppliedNavigationRail.Plan(Supplied() with { Icon = "🎓" }, "Course/L1");

        rail.Home.Icon.Should().Be("🎓", "the heading shows the indexed root's own icon");
        Groups(rail).Single(g => g.Path == "Course/L1").Icon.Should().Be("📘",
            "an entry that becomes a group keeps its icon on the heading instead of losing it "
            + "for having children");
        Groups(rail).Single(g => g.Path == "Course/L2").Icon.Should().BeNull(
            "no icon supplied renders a plain heading");
    }

    [Fact]
    public void ItRendersAsOneCollapsibleNavMenu()
    {
        var menu = SuppliedNavigationRail.Render(SuppliedNavigationRail.Plan(Supplied(), "Course/L1"));

        menu.Skin.Width.Should().Be(240);
        menu.Skin.Collapsible.Should().Be(true);
        // home link + the childless entry + one area per group
        menu.Areas.Should().HaveCount(4);
    }

    // ── #3406: the supplier's order is the rail's order ───────────────────────────────────────────

    /// <summary>
    /// A course whose reading order INTERLEAVES leaf pages and lesson folders — which is every real
    /// course. The lessons carry children (exercise, solution, quiz); the other three do not.
    /// </summary>
    private static NodeNavigation Interleaved() =>
        new("The Course",
        [
            new NodeNavigationEntry("Read me first", "Course/Readme"),
            new NodeNavigationEntry("Lesson 1", "Course/L1")
            {
                Children = [new NodeNavigationEntry("Lesson 1 Quiz", "Course/L1/Quiz")],
            },
            new NodeNavigationEntry("Interlude", "Course/Interlude"),
            new NodeNavigationEntry("Lesson 2", "Course/L2")
            {
                Children = [new NodeNavigationEntry("Lesson 2 Quiz", "Course/L2/Quiz")],
            },
            new NodeNavigationEntry("Wrap up", "Course/Wrap"),
        ])
        { TitlePath = Course };

    /// <summary>
    /// #3406. The plan used to hold two buckets — entries without children, then entries with —
    /// and render every one of the first before any of the second. The supplied order survived
    /// INSIDE each bucket and was lost between them, so a lesson folder could never precede a leaf
    /// page however it was numbered: AgenticOffice's four lessons, ordered 1–4, rendered tenth to
    /// thirteenth. Having children decides WHAT an entry becomes, never WHERE it goes.
    /// </summary>
    [Fact]
    public void TheSuppliedOrderSurvivesLeavesAndFoldersInterleaving()
    {
        var rail = SuppliedNavigationRail.Plan(Interleaved(), Course);

        ItemPaths(rail).Should().Equal(
            "Course/Readme", "Course/L1", "Course/Interlude", "Course/L2", "Course/Wrap");
    }

    /// <summary>
    /// The order does not depend on where the reader stands — expanding a group must not reorder
    /// the rail underneath them.
    /// </summary>
    [Fact]
    public void TheSuppliedOrderIsTheSameWhereverTheReaderStands()
    {
        var expected = new[]
        {
            "Course/Readme", "Course/L1", "Course/Interlude", "Course/L2", "Course/Wrap",
        };

        foreach (var standing in new[] { Course, "Course/Readme", "Course/L1", "Course/L1/Quiz", "Course/Wrap" })
            ItemPaths(SuppliedNavigationRail.Plan(Interleaved(), standing))
                .Should().Equal(expected, "from {0}", standing);
    }

    /// <summary>
    /// A leaf stays a link and a folder stays a collapsible group — the fix reorders the rail, it
    /// does not flatten it. Losing the groups would cost the collapsible lessons, which is exactly
    /// the satellite-side workaround #3406 rejected.
    /// </summary>
    [Fact]
    public void InterleavingKeepsEachEntryItsOwnKind()
    {
        var rail = SuppliedNavigationRail.Plan(Interleaved(), Course);

        Pages(rail).Select(p => p.Path).Should().Equal(
            "Course/Readme", "Course/Interlude", "Course/Wrap");
        Groups(rail).Select(g => g.Path).Should().Equal("Course/L1", "Course/L2");
        Groups(rail).Should().OnlyContain(g => g.Links.Count == 2,
            "a folder still carries its own link first, then its children");
    }

    /// <summary>
    /// And the menu gets one area per entry, in that same single pass — <c>Render</c> walks the one
    /// ordered sequence, so there is no second loop to append the groups behind the links.
    /// </summary>
    [Fact]
    public void TheRenderedMenuHasOneAreaPerEntryInPlanOrder()
    {
        var rail = SuppliedNavigationRail.Plan(Interleaved(), Course);
        var menu = SuppliedNavigationRail.Render(rail);

        menu.Areas.Should().HaveCount(rail.Items.Count + 1,
            "the heading link plus one area per supplied entry — nothing dropped, nothing doubled");
    }
}
