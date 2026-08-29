using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using System.IO;
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

    // ── The catalog READ: storm-safe and anchored (#2640) ────────────────────────────────────────

    /// <summary>
    /// 🚨 The catalog read must be a <c>path:</c> QUERY, not a point read.
    ///
    /// <para>There is deliberately no seeded catalog, so ABSENT is the normal state for every
    /// context on almost every mesh — and <c>RenderMenus</c> is a global predicate renderer
    /// (<c>WithRenderer(_ =&gt; true, …)</c>) that reinstalls this stream once per context on EVERY
    /// area render. A point <c>GetMeshNodeStream</c> probe of a maybe-absent path answers NotFound:
    /// one <c>[ROUTE] NotFound</c> warning plus a DeliveryFailure NACK per probe, damped only by a
    /// negative-cache window that expires and re-probes forever. <c>PlatformUpdateStatus.Observe</c>
    /// states the rule outright — "storm-safe existence GetQuery (empty-on-absent) — NEVER a point
    /// GetMeshNodeStream probe of a maybe-absent path".</para>
    ///
    /// <para>This asserts the QUERY STRING because that is the part a caller can get wrong silently:
    /// the shape decides both the storm behaviour and the partition routing.</para>
    /// </summary>
    [Fact]
    public void CatalogQuery_IsAnExactPathRead_SoAnAbsentCatalogIsNoRowsRatherThanNotFound()
    {
        var query = MenuPresentationOverlay.CatalogQuery("Node");

        Assert.StartsWith("path:Admin/Menu/Node", query);
        // Default scope for a `path:` query is Exact — no scope token means the one node, which is
        // what an existence check wants. An explicit widening here would read siblings too.
        Assert.DoesNotContain("scope:", query);
    }

    /// <summary>
    /// The read must stay ANCHORED to the Admin partition. It is anchored by its first path segment,
    /// which is the ONLY way in: <c>admin</c> is deliberately excluded from cross-schema search, so a
    /// <c>namespace:</c>-shaped read would fan out over every partition AND still miss the catalog.
    /// </summary>
    [Fact]
    public void CatalogQuery_IsAnchoredToTheAdminPartition_ByPathNotNamespace()
    {
        foreach (var context in new[] { "Node", "Mesh", "AI", "" })
        {
            var query = MenuPresentationOverlay.CatalogQuery(context);
            var firstSegment = query["path:".Length..].Split(' ')[0].Split('/')[0];
            Assert.Equal(MenuPresentation.CatalogPartition, firstSegment);
            Assert.DoesNotContain("namespace:", query);
        }
    }

    /// <summary>
    /// The projection must NAME <c>content</c>. A <c>select:</c> that omits it yields a node whose
    /// <c>Content</c> is silently null, so a catalog that exists would read as "no catalog" and the
    /// overlay would quietly stop applying — the failure this whole class exists to make visible.
    /// </summary>
    [Fact]
    public void CatalogQuery_ProjectsContent_OrAnExistingCatalogReadsAsAbsent()
    {
        Assert.Contains("select:", MenuPresentationOverlay.CatalogQuery("Node"));
        Assert.Contains("content", MenuPresentationOverlay.CatalogProjection);
    }

    /// <summary>
    /// The synced-query id is STABLE per context and distinct across contexts. Both halves matter:
    /// an id composed per call would open a new upstream on every area render (defeating the cache
    /// that makes the per-render re-subscribe cheap), and a shared id would serve the Node
    /// dropdown's catalog to the Mesh dropdown.
    /// </summary>
    [Fact]
    public void CatalogQueryId_IsStablePerContext_AndDistinctAcrossContexts()
    {
        Assert.Equal(MenuPresentationOverlay.CatalogQueryId("Node"), MenuPresentationOverlay.CatalogQueryId("Node"));
        Assert.NotEqual(MenuPresentationOverlay.CatalogQueryId("Node"), MenuPresentationOverlay.CatalogQueryId("Mesh"));
        // The unnamed context resolves through PathFor, so it cannot collide with "Node" either.
        Assert.NotEqual(MenuPresentationOverlay.CatalogQueryId(""), MenuPresentationOverlay.CatalogQueryId("Node"));
    }

    /// <summary>
    /// 🚨 The guard that pins the PRIMITIVE, because the string tests above cannot see it: a
    /// point read and a query produce the same catalog when the node exists, and differ only when
    /// it does NOT — which here is the normal state.
    ///
    /// <para>The rule is already written down, in <c>PlatformUpdateStatus.Observe</c>: "Absence is
    /// detected with the storm-safe existence <c>GetQuery</c> (empty-on-absent) — NEVER a point
    /// <c>GetMeshNodeStream</c> probe of a maybe-absent path, which NotFound-resubscribe-storms."
    /// This overlay broke it on a far hotter path than the one that rule was written for:
    /// <c>RenderMenus</c> is a global predicate renderer, so it reinstalled the probe once per menu
    /// context on EVERY area render, and each probe of the deliberately-absent catalog cost a
    /// <c>[ROUTE] NotFound</c> warning and a DeliveryFailure NACK.</para>
    ///
    /// <para>A source guard rather than a behavioural assertion is deliberate: reproducing the
    /// difference needs a live hub, a routing grain and an absent node, and a test that heavy would
    /// be the first one silenced. This one fails the moment the primitive is swapped back, which is
    /// the only regression that matters. If the file is restructured, re-point it — do not delete
    /// it because it went red.</para>
    /// </summary>
    [Fact]
    public void CatalogStream_UsesTheStormSafeExistenceQuery_NeverAPointReadOfAMaybeAbsentPath()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "MeshWeaver.Graph", "Configuration", "MenuPresentationOverlay.cs"));

        // The body must reach the catalog through the synced query surface…
        Assert.Contains("GetQuery(CatalogQueryId(context), CatalogQuery(context))", source);

        // …and must not point-probe it. Mentions inside comments are how the rule explains itself,
        // so only CODE lines are considered.
        var pointReads = source
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//") && !line.StartsWith("///") && !line.StartsWith("*"))
            .Where(line => line.Contains("GetMeshNodeStream"))
            .ToList();

        Assert.True(pointReads.Count == 0,
            "MenuPresentationOverlay must not point-read Admin/Menu/{context}: the catalog is absent "
            + "by design and RenderMenus reinstalls this stream on every area render, so a point read "
            + "is a permanent NotFound-resubscribe storm (#2640). Found: "
            + string.Join(" | ", pointReads));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
