using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Where the two halves of the menu meet: <see cref="MenuPresentationOverlay.Apply"/> (nesting from
/// DATA — a catalog entry's <c>Parent</c>) and <c>NodeMenuItemsExtensions.Normalize</c> (child
/// ordering + empty-group pruning). <c>RenderMenus</c> runs them in exactly this order, and these
/// tests pin that composition rather than either half alone.
///
/// <para>The property that matters: <b>a grouping created purely by data must behave exactly like a
/// code-defined one</b>. Normalize used to run on the pre-overlay slice, which meant it sorted and
/// pruned a tree the overlay had not built yet — data-created groupings silently skipped both rules.</para>
/// </summary>
public class MenuNestingCompositionTest
{
    private static IReadOnlyCollection<NodeMenuItemDefinition> Items(params NodeMenuItemDefinition[] items) => items;

    /// <summary>The RenderMenus order: overlay first, then normalize. Localization follows, and is irrelevant here.</summary>
    private static IReadOnlyCollection<NodeMenuItemDefinition> Compose(
        IReadOnlyCollection<NodeMenuItemDefinition> items, MenuPresentation? catalog, List<string>? skipped = null)
        => NodeMenuItemsExtensions.Normalize(
            MenuPresentationOverlay.Apply(items, catalog, "en", r => skipped?.Add(r)));

    private static readonly NodeMenuItemDefinition Edit = new("Edit", "Edit", Icon: "✏️", Order: 10);
    private static readonly NodeMenuItemDefinition Pdf = new("PDF", "ExportPdf", Icon: "📄", Order: 27);
    private static readonly NodeMenuItemDefinition Email = new("Email", "SendDocument", Icon: "📤", Order: 28);
    private static readonly NodeMenuItemDefinition Docx = new("DOCX", "ExportDocx", Icon: "📝", Order: 29);

    private static MenuPresentation Catalog(params MenuEntryPresentation[] entries)
        => new("Node", entries);

    [Fact]
    public void DataCreatedGrouping_RendersAsASubMenu()
    {
        // Email and DOCX are moved under PDF purely by catalog edit.
        var result = Compose(Items(Edit, Pdf, Email, Docx), Catalog(
            new MenuEntryPresentation("SendDocument", Parent: "ExportPdf"),
            new MenuEntryPresentation("ExportDocx", Parent: "ExportPdf")));

        result.Select(i => i.Area).Should().Equal(["Edit", "ExportPdf"]);
        var parent = result.Single(i => i.Area == "ExportPdf");
        parent.IsSubmenuParent.Should().BeTrue("an entry that gained children is a sub-menu parent");
        parent.Children!.Select(c => c.Area).Should().Equal(["SendDocument", "ExportDocx"]);
    }

    [Fact]
    public void DataCreatedGrouping_ChildrenAreSortedByOrder()
    {
        // Declared DOCX(29) before Email(28); Order must win at depth just as it does at the top.
        var result = Compose(Items(Pdf, Docx, Email), Catalog(
            new MenuEntryPresentation("ExportDocx", Parent: "ExportPdf"),
            new MenuEntryPresentation("SendDocument", Parent: "ExportPdf")));

        result.Single().Children!.Select(c => c.Label).Should().Equal(["Email", "DOCX"]);
    }

    [Fact]
    public void DataCreatedGrouping_HonoursACatalogOrderOverrideOnAChild()
    {
        var result = Compose(Items(Pdf, Email, Docx), Catalog(
            new MenuEntryPresentation("SendDocument", Parent: "ExportPdf", Order: 99),
            new MenuEntryPresentation("ExportDocx", Parent: "ExportPdf")));

        result.Single().Children!.Select(c => c.Label).Should().Equal(["DOCX", "Email"],
            "a child re-ordered by data must re-sort inside the sub-menu, not keep its compiled slot");
    }

    [Fact]
    public void ADanglingParent_LeavesTheEntryTopLevel_AndNeverLosesIt()
    {
        var skipped = new List<string>();
        var result = Compose(Items(Edit, Pdf), Catalog(
            new MenuEntryPresentation("ExportPdf", Parent: "NoSuchArea")), skipped);

        result.Select(i => i.Area).Should().Equal(["Edit", "ExportPdf"],
            "#1252's contract: an unresolvable Parent keeps the entry visible at top level");
        // …and Normalize must not then prune it: it is not a group, it has a real area.
        result.Single(i => i.Area == "ExportPdf").IsGroup.Should().BeFalse();
        skipped.Should().ContainSingle().Which.Should().Contain("NoSuchArea");
    }

    [Fact]
    public void ACodeDefinedGroup_EmptiedByCatalogHiding_IsPruned()
    {
        // The group and its children come from a PROVIDER; the catalog hides the children.
        // (The overlay descends into Children, so a nested entry stays addressable by its area.)
        var group = new NodeMenuItemDefinition(
            "Export", NodeMenuItemDefinition.GroupArea("Export"), Icon: "📦", Order: 27,
            Children: [Pdf, Email, Docx]);

        var result = Compose(Items(Edit, group), Catalog(
            new MenuEntryPresentation("ExportPdf", Hidden: true),
            new MenuEntryPresentation("SendDocument", Hidden: true),
            new MenuEntryPresentation("ExportDocx", Hidden: true)));

        result.Select(i => i.Area).Should().Equal(["Edit"],
            "a group whose every child was hidden opens onto nothing — it must not survive as a dead row");
    }

    [Fact]
    public void ACodeDefinedGroup_KeepsSurvivingChildren()
    {
        var group = new NodeMenuItemDefinition(
            "Export", NodeMenuItemDefinition.GroupArea("Export"), Icon: "📦", Order: 27,
            Children: [Pdf, Email, Docx]);

        var result = Compose(Items(group), Catalog(new MenuEntryPresentation("ExportDocx", Hidden: true)));

        result.Single().Children!.Select(c => c.Label).Should().Equal(["PDF", "Email"]);
    }

    [Fact]
    public void ACodeDefinedGroup_IsAddressableByItsOwnArea()
    {
        // The whole reason a group carries `_group:{name}` rather than one shared sentinel.
        var group = new NodeMenuItemDefinition(
            "Export", NodeMenuItemDefinition.GroupArea("Export"), Icon: "📦", Order: 27,
            Children: [Pdf]);

        var result = Compose(Items(Edit, group), Catalog(
            new MenuEntryPresentation(
                NodeMenuItemDefinition.GroupArea("Export"),
                Labels: new Dictionary<string, string> { ["en"] = "Send elsewhere" },
                Icon: "🚚",
                Order: 5)));

        var renamed = result.First();
        renamed.Label.Should().Be("Send elsewhere", "a group must be re-wordable by data like any other entry");
        renamed.Icon.Should().Be("🚚");
        renamed.Area.Should().Be(NodeMenuItemDefinition.GroupArea("Export"));
        renamed.Children!.Should().ContainSingle();
    }

    [Fact]
    public void ACodeDefinedGroup_CanBeHiddenWholesale()
    {
        var group = new NodeMenuItemDefinition(
            "Export", NodeMenuItemDefinition.GroupArea("Export"), Icon: "📦", Order: 27,
            Children: [Pdf, Email]);

        var result = Compose(Items(Edit, group), Catalog(
            new MenuEntryPresentation(NodeMenuItemDefinition.GroupArea("Export"), Hidden: true)));

        result.Select(i => i.Area).Should().Equal(["Edit"],
            "hiding a compiled group removes it and the subtree it owns");
    }

    [Fact]
    public void NoCatalog_StillNormalizes()
    {
        // Normalize must not depend on the overlay having done anything: with no catalog at all the
        // overlay returns the input untouched, and the compiled tree still gets sorted and pruned.
        var group = new NodeMenuItemDefinition(
            "Export", NodeMenuItemDefinition.GroupArea("Export"), Order: 27,
            Children: [Docx, Pdf, Email]);
        var emptyGroup = new NodeMenuItemDefinition(
            "Nothing", NodeMenuItemDefinition.GroupArea("Nothing"), Order: 60, Children: []);

        var result = Compose(Items(group, emptyGroup), catalog: null);

        result.Select(i => i.Label).Should().Equal(["Export"], "the empty group is pruned");
        result.Single().Children!.Select(c => c.Label).Should().Equal(["PDF", "Email", "DOCX"]);
    }
}
