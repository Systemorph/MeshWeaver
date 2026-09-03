using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Section dividers are DERIVED from the finished menu, never declared by a provider
/// (<c>Doc/Architecture/MenuContributionBoundary</c>, ask 4 of #3102 / blocker 3).
///
/// <para>The defect these pin: <c>DefaultNodeMenuProvider</c> used to decide its own dividers from
/// the items IT emitted — before the merge with every other provider, and before
/// <see cref="MenuPresentationOverlay"/>, which can hide any of them. Both directions were live and
/// user-visible: an admin hiding the last entry of a section left a rule with nothing on one side,
/// and on a viewer's own home — where every compiled section-1 entry is suppressed — a contributed
/// entry in that band ran straight into Files with no rule at all.</para>
///
/// <para><see cref="NodeMenuItemsExtensions.WithSectionDividers"/> runs last, on the merged,
/// overlaid, normalized list, so neither is expressible any more.</para>
/// </summary>
public class MenuSectionDividerTest
{
    private const string Node = NodeMenuItemsExtensions.NodeMenuContext;
    private const string Mesh = NodeMenuItemsExtensions.MeshMenuContext;
    private const string Separator = NodeMenuItemDefinition.SeparatorArea;

    private static NodeMenuItemDefinition Item(string area, int order) => new(area, area, Order: order);

    private static NodeMenuItemDefinition Divider(int order) => new("", Separator, Order: order);

    private static IReadOnlyList<string> Areas(IReadOnlyList<NodeMenuItemDefinition> items)
        => [.. items.Select(i => i.Area)];

    /// <summary>The everyday shape: three populated sections, two dividers, at 20 and 40.</summary>
    [Fact]
    public void ThreePopulatedSections_GetOneDividerAtEachSeam()
    {
        var result = NodeMenuItemsExtensions.WithSectionDividers(
            [Item("Edit", 10), Item("Delete", 18), Item("Files", 30), Item("Versions", 32), Item("Recycle", 50)],
            Node);

        Areas(result).Should().Equal("Edit", "Delete", Separator, "Files", "Versions", Separator, "Recycle");
        result.Where(i => i.Area == Separator).Select(i => i.Order).Should().Equal(20, 40);
    }

    /// <summary>
    /// The viewer's OWN home: every compiled section-1 entry is suppressed, so the old
    /// provider-side rule emitted no divider at 20 — and a contributed entry in that band then had
    /// nothing between it and Files. Derivation sees the merged list and gets it right.
    /// </summary>
    [Fact]
    public void ContributedEntryAloneInASection_StillGetsItsDivider()
    {
        var result = NodeMenuItemsExtensions.WithSectionDividers(
            [Item("PluginFrontDoor", 15), Item("Files", 30), Item("Data", 31)],
            Node);

        Areas(result).Should().Equal("PluginFrontDoor", Separator, "Files", "Data");
    }

    /// <summary>
    /// The overlay hid the last entry of section 2. Nothing is left between the seams, so ONE
    /// divider spans both crossed boundaries — never two adjacent rules.
    /// </summary>
    [Fact]
    public void AnEmptyMiddleSection_ProducesOneDivider_NotTwo()
    {
        var result = NodeMenuItemsExtensions.WithSectionDividers(
            [Item("Edit", 10), Item("Recycle", 50)], Node);

        Areas(result).Should().Equal("Edit", Separator, "Recycle");
        Assert.Equal(20, result.Single(i => i.Area == Separator).Order);
    }

    /// <summary>A divider can only ever go BETWEEN two surviving entries — so a single-section
    /// menu, and a single-entry menu, carry none at all.</summary>
    [Theory]
    [InlineData(10, 18)]
    [InlineData(30, 34)]
    [InlineData(50, 50)]
    public void OneSectionOnly_HasNoDivider(int first, int second)
    {
        var result = NodeMenuItemsExtensions.WithSectionDividers(
            [Item("a", first), Item("b", second)], Node);

        result.Should().NotContain(i => i.Area == Separator);
        Areas(result).Should().Equal("a", "b");
    }

    /// <summary>The empty menu stays empty — no leading rule, nothing at all.</summary>
    [Fact]
    public void AnEmptyMenu_StaysEmpty()
        => NodeMenuItemsExtensions.WithSectionDividers([], Node).Should().BeEmpty();

    /// <summary>
    /// 🚨 A provider that still emits its own divider cannot reintroduce the defect: in a banded
    /// context every incoming <c>_separator</c> is dropped and the seams are re-derived. The
    /// in-mesh per-NodeType delegates this build cannot recompile are exactly that case.
    /// </summary>
    [Fact]
    public void ADeclaredDividerInABandedContext_IsReplacedByTheDerivedOne()
    {
        var result = NodeMenuItemsExtensions.WithSectionDividers(
            // A stale provider's guess: a leading rule, and one at a seam that carries no section change.
            [Divider(5), Item("Edit", 10), Divider(12), Item("Delete", 18), Item("Files", 30)],
            Node);

        Areas(result).Should().Equal("Edit", "Delete", Separator, "Files");
    }

    /// <summary>
    /// A FLAT context (Mesh, AI, a TopBar-declared menu) declares no bands, so nothing is derived —
    /// but a divider left dangling by permission filtering or the overlay is still pruned. Leading,
    /// doubled and trailing rules are all unrenderable shapes.
    /// </summary>
    [Fact]
    public void AFlatContext_KeepsDeclaredDividers_ButNeverADanglingOne()
    {
        var result = NodeMenuItemsExtensions.WithSectionDividers(
            [Divider(0), Item("Create", 1), Divider(2), Divider(3), Item("Export", 26), Divider(30)],
            Mesh);

        Areas(result).Should().Equal("Create", Separator, "Export");
    }

    /// <summary>Control arm for the flat case: with no dividers to prune, a flat context is passed
    /// through untouched — so the assertions above cannot be green because everything was dropped.</summary>
    [Fact]
    public void AFlatContext_WithoutDividers_IsUnchanged()
    {
        var input = new List<NodeMenuItemDefinition> { Item("Create", 0), Item("Import", 1), Item("Export", 26) };

        Areas(NodeMenuItemsExtensions.WithSectionDividers(input, Mesh))
            .Should().Equal("Create", "Import", "Export");
    }
}
