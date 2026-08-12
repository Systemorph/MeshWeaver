using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The data half of the node menu: <see cref="MenuPresentation"/> re-words, re-icons, re-orders,
/// groups and hides entries that the compiled providers emit — so changing a menu label is a node
/// edit rather than a CI + image + rollout cycle.
///
/// <para>These tests pin the two properties the design rests on: (1) the overlay can only ever
/// SUBTRACT or re-dress — it never introduces an entry, so a catalog edit cannot widen access past
/// the providers' permission gates; (2) every malformed input degrades to the compiled default AND
/// names itself, so a bad edit can never leave a viewer with no menu.</para>
/// </summary>
public class MenuPresentationOverlayTest
{
    private static IReadOnlyCollection<NodeMenuItemDefinition> Items(params NodeMenuItemDefinition[] items) => items;

    private static readonly NodeMenuItemDefinition Edit =
        new("Edit", "Edit", Icon: "✏️", Order: 10) { LabelKey = "menu.edit" };

    private static readonly NodeMenuItemDefinition Delete =
        new("Delete", "Delete", Icon: "🗑️", Order: 18) { LabelKey = "menu.delete" };

    private static readonly NodeMenuItemDefinition Pdf =
        new("Export to PDF", "ExportPdf", Order: 27);

    [Fact]
    public void NoCatalog_LeavesTheCompiledMenuUntouched()
    {
        var items = Items(Edit, Delete);

        // A portal with no catalog node renders exactly the compiled menu.
        Assert.Equal(items, MenuPresentationOverlay.Apply(items, null, "en"));
    }

    [Fact]
    public void EmptyCatalog_LeavesTheCompiledMenuUntouched()
    {
        var items = Items(Edit, Delete);

        Assert.Equal(items, MenuPresentationOverlay.Apply(items, new MenuPresentation("Node"), "en"));
    }

    [Fact]
    public void DataLabel_ReplacesTheCompiledLabel_AndClearsTheKeySoTranslationDoesNotUndoIt()
    {
        var catalog = new MenuPresentation("Node", [
            new MenuEntryPresentation("Edit", Labels: new Dictionary<string, string> { ["en"] = "Modify" })
        ]);

        var result = Assert.Single(MenuPresentationOverlay.Apply(Items(Edit), catalog, "en"));

        Assert.Equal("Modify", result.Label);
        // The central Localized(access) pass runs AFTER the overlay — leaving LabelKey set would
        // translate the override straight back to the compiled text.
        Assert.Null(result.LabelKey);
    }

    [Fact]
    public void DataLabel_ResolvesPerViewerLocale()
    {
        var catalog = new MenuPresentation("Node", [
            new MenuEntryPresentation("Edit", Labels: new Dictionary<string, string>
            {
                ["en"] = "Modify",
                ["de"] = "Ändern",
            })
        ]);

        Assert.Equal("Ändern", Assert.Single(MenuPresentationOverlay.Apply(Items(Edit), catalog, "de")).Label);
        Assert.Equal("Modify", Assert.Single(MenuPresentationOverlay.Apply(Items(Edit), catalog, "en")).Label);
    }

    [Fact]
    public void DataLabel_FallsBackRegionToBaseLanguage_ThenToEnglish_ThenToTheCompiledLabel()
    {
        var catalog = new MenuPresentation("Node", [
            new MenuEntryPresentation("Edit", Labels: new Dictionary<string, string>
            {
                ["en"] = "Modify",
                ["de"] = "Ändern",
            })
        ]);

        // de-CH has no entry of its own → base language.
        Assert.Equal("Ändern", Assert.Single(MenuPresentationOverlay.Apply(Items(Edit), catalog, "de-CH")).Label);
        // An unlisted language → English, never an empty entry.
        Assert.Equal("Modify", Assert.Single(MenuPresentationOverlay.Apply(Items(Edit), catalog, "fr")).Label);

        // No usable text at all → the compiled label AND its translation key survive.
        var blank = new MenuPresentation("Node", [
            new MenuEntryPresentation("Edit", Labels: new Dictionary<string, string> { ["de"] = "   " })
        ]);
        var result = Assert.Single(MenuPresentationOverlay.Apply(Items(Edit), blank, "en"));
        Assert.Equal("Edit", result.Label);
        Assert.Equal("menu.edit", result.LabelKey);
    }

    [Fact]
    public void IconAndOrder_AreOverridden_AndTheMenuIsReSorted()
    {
        // The providers sorted the COMPILED order before any override ran, so an entry moved to the
        // front only actually moves if the overlay re-sorts.
        var catalog = new MenuPresentation("Node", [
            new MenuEntryPresentation("Delete", Icon: "❌", Order: 1)
        ]);

        var result = MenuPresentationOverlay.Apply(Items(Edit, Delete), catalog, "en").ToList();

        Assert.Equal("Delete", result[0].Area);
        Assert.Equal("❌", result[0].Icon);
        Assert.Equal("Edit", result[1].Area);
    }

    [Fact]
    public void Hidden_RemovesTheEntry_AndNothingElse()
    {
        var catalog = new MenuPresentation("Node", [new MenuEntryPresentation("Delete", Hidden: true)]);

        Assert.Equal(["Edit"],
            MenuPresentationOverlay.Apply(Items(Edit, Delete), catalog, "en").Select(i => i.Area));
    }

    [Fact]
    public void Parent_NestsTheEntryAsASubMenuChild()
    {
        var catalog = new MenuPresentation("Node", [
            new MenuEntryPresentation("ExportPdf", Parent: "Edit")
        ]);

        var result = MenuPresentationOverlay.Apply(Items(Edit, Pdf), catalog, "en").ToList();

        // The child is no longer top-level.
        Assert.Equal(["Edit"], result.Select(i => i.Area));
        Assert.Equal("ExportPdf", Assert.Single(result[0].Children!).Area);
    }

    [Fact]
    public void UnresolvableParent_KeepsTheEntryVisible_AndNamesIt()
    {
        var skipped = new List<string>();
        var catalog = new MenuPresentation("Node", [
            new MenuEntryPresentation("ExportPdf", Parent: "NoSuchArea")
        ]);

        var result = MenuPresentationOverlay.Apply(Items(Edit, Pdf), catalog, "en", skipped.Add);

        // A typo in Parent must never make an entry disappear.
        Assert.Equal(["Edit", "ExportPdf"], result.Select(i => i.Area).OrderBy(a => a, StringComparer.Ordinal));
        var reason = Assert.Single(skipped);
        Assert.Contains("ExportPdf", reason);
        Assert.Contains("NoSuchArea", reason);
    }

    [Fact]
    public void SelfParent_KeepsTheEntryVisible_AndNamesIt()
    {
        var skipped = new List<string>();
        var catalog = new MenuPresentation("Node", [new MenuEntryPresentation("Edit", Parent: "Edit")]);

        Assert.Equal(["Edit"],
            MenuPresentationOverlay.Apply(Items(Edit), catalog, "en", skipped.Add).Select(i => i.Area));
        Assert.Contains("Edit", Assert.Single(skipped));
    }

    [Fact]
    public void EntryForAnAreaNobodyEmits_IsInert()
    {
        // The overlay is override-only: naming an unknown area cannot conjure a menu item, which is
        // what keeps a catalog edit from bypassing the providers' permission gates.
        var catalog = new MenuPresentation("Node", [
            new MenuEntryPresentation("SecretAdminAction",
                Labels: new Dictionary<string, string> { ["en"] = "Escalate" })
        ]);

        Assert.Equal(["Edit"],
            MenuPresentationOverlay.Apply(Items(Edit), catalog, "en").Select(i => i.Area));
    }

    [Fact]
    public void MalformedEntries_AreSkippedAndNamed_NeverSilentlySwallowed()
    {
        var skipped = new List<string>();
        var catalog = new MenuPresentation("Node", [
            new MenuEntryPresentation("", Icon: "?"),          // no area
            new MenuEntryPresentation("Edit", Icon: "1"),
            new MenuEntryPresentation("Edit", Icon: "2"),      // duplicate
        ]);

        var result = Assert.Single(MenuPresentationOverlay.Apply(Items(Edit), catalog, "en", skipped.Add));

        // The first entry for an area wins, deterministically.
        Assert.Equal("1", result.Icon);
        Assert.Equal(2, skipped.Count);
        Assert.Contains(skipped, s => s.Contains("no Area"));
        Assert.Contains(skipped, s => s.Contains("repeats Area"));
    }

    [Fact]
    public void AnEntryThatSetsNothing_IsANoOp()
    {
        var catalog = new MenuPresentation("Node", [new MenuEntryPresentation("Edit")]);

        // Every field is optional and null means "leave the compiled value alone".
        Assert.Equal(Edit, Assert.Single(MenuPresentationOverlay.Apply(Items(Edit), catalog, "en")));
    }

    [Fact]
    public void CatalogPath_IsPartitionScopedPerContext()
    {
        Assert.Equal("Admin/Menu/Node", MenuPresentation.PathFor("Node"));
        Assert.Equal("Admin/Menu/Mesh", MenuPresentation.PathFor("Mesh"));
        // The unnamed root $Menu slot must not silently share the Node dropdown's catalog.
        Assert.Equal("Admin/Menu/Default", MenuPresentation.PathFor(""));
    }
}
