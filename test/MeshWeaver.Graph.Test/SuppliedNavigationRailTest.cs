using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The left-hand rail a module supplies through <see cref="INodeNavigationProvider"/> — what it
/// CONTAINS, asserted on the plan rather than on a control tree whose children are protected.
///
/// <para>Three defects were reported together on a live course (2026-08-19) and every one of them
/// was invisible to a control-level test: the entry the reader stood on dropped out of the tree,
/// clicking the index title collapsed the whole index, and a long index rendered as an
/// undifferentiated wall of links with no visible expanders. Each has a case here.</para>
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

    private static IEnumerable<SuppliedNavigationRail.RailLink> AllLinks(SuppliedNavigationRail.Rail rail)
        => new[] { rail.Home }.Concat(rail.Pages).Concat(rail.Groups.SelectMany(g => g.Links));

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
        rail.Groups.Should().HaveCount(2, "the entries are TOP-LEVEL groups, siblings of the title");
        rail.Groups.Should().NotContain(g => g.Label == rail.Home.Label,
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
        rail.Groups.Single().Links.Should().Contain(l => l.IsCurrent,
            "and it sits inside its own group, in the same slot as when it was not current");
    }

    [Fact]
    public void AnEntryWithChildrenIsAGroupAndItsOwnFirstLink()
    {
        var rail = SuppliedNavigationRail.Plan(Supplied(), "Course/L1");
        var lesson = rail.Groups.Single(g => g.Path == "Course/L1");

        lesson.Links.Select(l => l.Path).Should().Equal(
            "Course/L1", "Course/L1/Quiz", "Course/L1/Solution");
        lesson.Links[0].Href.Should().Be("/Course/L1",
            "a group HEADING has no active state to carry the position — the self-link does");
    }

    [Fact]
    public void OnlyTheGroupTheReaderIsInsideIsExpanded()
    {
        foreach (var inside in new[] { "Course/L1", "Course/L1/Quiz", "Course/L1/Solution/Deep" })
            SuppliedNavigationRail.Plan(Supplied(), inside)
                .Groups.Where(g => g.Expanded).Select(g => g.Path)
                .Should().Equal(["Course/L1"], "from {0}", inside);

        SuppliedNavigationRail.Plan(Supplied(), Course).Groups
            .Should().OnlyContain(g => !g.Expanded,
                "on the index root every group is collapsed — which is what makes its expander visible");
    }

    [Fact]
    public void AChildlessEntryIsAFlatLink()
    {
        var rail = SuppliedNavigationRail.Plan(Supplied(), Course);

        rail.Pages.Select(p => p.Path).Should().Equal("Course/Exercises");
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
        rail.Groups.Should().HaveCount(2, "the index itself is unaffected — only the dead link is withheld");
    }

    [Fact]
    public void TheHeadingAndTheGroupsKeepTheirIcons()
    {
        var rail = SuppliedNavigationRail.Plan(Supplied() with { Icon = "🎓" }, "Course/L1");

        rail.Home.Icon.Should().Be("🎓", "the heading shows the indexed root's own icon");
        rail.Groups.Single(g => g.Path == "Course/L1").Icon.Should().Be("📘",
            "an entry that becomes a group keeps its icon on the heading instead of losing it "
            + "for having children");
        rail.Groups.Single(g => g.Path == "Course/L2").Icon.Should().BeNull(
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
}
