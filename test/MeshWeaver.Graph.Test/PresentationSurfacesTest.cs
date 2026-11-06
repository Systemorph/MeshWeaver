using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Presentation mode (issue #1803) on the SURFACES the issue names — the home's viewer-scoped
/// surfaces (the Apps section, the Content section's Pinned tab and its one All category, the
/// Shared-with-me band) and the node menu that marks them.
///
/// <para>The Pinned tab and the Shared band filter at the point the QUERY is built rather than
/// only where a card is painted, and that is the interesting part: a pinned path and a shared-band
/// target are INTERPOLATED INTO the control's query string, which the view exposes in its
/// search-options editor and carries in the <c>hq=</c> parameter of "open in search". A marked name
/// reaching the address bar mid-presentation is the leak, whether or not a card for it is ever
/// drawn. The Apps records are the viewer's own — their query is generic, so they screen at paint,
/// keyed by each tile's navigation target.</para>
///
/// <para>The OG-card surface is NOT here: <c>MeshWeaver.OgCard</c> left the platform in #1975, so
/// screening it is a follow-up in the repo that owns it now.</para>
/// </summary>
public class PresentationSurfacesTest
{
    private const string Owner = "alice";

    private static PresentationScreen Active(params string[] marks)
        => PresentationScreen.For(true, marks);

    private static string QueryOf(UiControl? control)
        => ((MeshSearchControl)control!).HiddenQuery!.ToString()!;

    // ── Pinned tiles ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PinnedTiles_DropAMarkedPin_AndKeepTheRest()
    {
        var user = new User { PinnedPaths = ["Acme", "Northwind", "Doc/Guide"] };

        var query = QueryOf(UserActivityLayoutAreas.BuildPinnedItems(user, screen: Active("Acme")));

        query.Should().NotContain("Acme", "the marked name must not even reach the query string");
        query.Should().Contain("Northwind");
        query.Should().Contain("Doc/Guide");
    }

    [Fact]
    public void PinnedTiles_DropAPinInsideAMarkedSpace()
    {
        var user = new User { PinnedPaths = ["Acme/Q3-Renewal", "Northwind"] };

        QueryOf(UserActivityLayoutAreas.BuildPinnedItems(user, screen: Active("Acme")))
            .Should().NotContain("Q3-Renewal").And.Contain("Northwind");
    }

    [Fact]
    public void PinnedTiles_CollapseWhenEveryPinIsMarked()
    {
        var user = new User { PinnedPaths = ["Acme", "Acme/Q3-Renewal"] };

        UserActivityLayoutAreas.BuildPinnedItems(user, screen: Active("Acme"))
            .Should().BeNull("an empty Pinned region collapses rather than rendering a bare title");
    }

    [Fact]
    public void PinnedTiles_AreUntouchedWhenTheModeIsOff()
    {
        var user = new User { PinnedPaths = ["Acme", "Northwind"] };

        // Marked, but presentation mode is off — the whole undo story.
        QueryOf(UserActivityLayoutAreas.BuildPinnedItems(
                user, screen: PresentationScreen.For(false, ["Acme"])))
            .Should().Contain("Acme").And.Contain("Northwind");
        // And with no screen supplied at all, which is every existing caller.
        QueryOf(UserActivityLayoutAreas.BuildPinnedItems(user))
            .Should().Contain("Acme").And.Contain("Northwind");
    }

    [Fact]
    public void PinnedTiles_OfAnotherUserAreUnaffected()
    {
        var bob = new User { PinnedPaths = ["Acme", "Northwind"] };

        // Bob is presenting too — with his OWN marks, which say nothing about Acme.
        QueryOf(UserActivityLayoutAreas.BuildPinnedItems(bob, screen: Active("Contoso")))
            .Should().Contain("Acme");
    }

    // ── The home's sections: Apps, then Spaces ─────────────────────────────────────────────────

    private static string SpacesBandQuery(UiControl home) =>
        ((System.Collections.IEnumerable)typeof(StackControl).BaseType!
            .GetProperty("Views", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(home)!)
        .OfType<MeshSearchControl>().Single(b => b.Title!.ToString() == "Spaces")
        .HiddenQuery!.ToString()!;

    [Fact]
    public void SpacesBand_ScreensMarkedSpacesAndGrants_BeforeTheQueryIsBuilt()
    {
        // The space manifest and the grants are interpolated into the Spaces band's query string,
        // which the search view exposes in its options editor and in the `hq=` URL — so a marked
        // name must be dropped BEFORE the query exists, not only where its tile is painted.
        var user = new User { SpacePaths = ["Acme", "Globex"], SharedPaths = ["Acme/Deals", "Northwind/Sales"] };

        var marked = SpacesBandQuery(UserActivityLayoutAreas.BuildHome(Owner, user: user, screen: Active("Acme")));
        marked.Should().NotContain("Acme").And.Contain("Globex").And.Contain("Northwind/Sales");

        var plain = SpacesBandQuery(UserActivityLayoutAreas.BuildHome(Owner, user: user));
        plain.Should().Contain("Acme").And.Contain("Acme/Deals");
    }

    [Fact]
    public void SharedBand_DropsAMarkedTarget()
    {
        var painted = Active("Acme").Retain(["Acme/Deals", "Northwind/Sales"]);
        painted.Should().Equal("Northwind/Sales");

        var band = UserActivityLayoutAreas.BuildSharedBand(painted, locale: null)!;
        band.HiddenQuery!.ToString()
            .Should().NotContain("Acme").And.Contain("Northwind/Sales");
    }

    [Fact]
    public void AppsBand_QueryNeverCarriesAppPaths()
    {
        // The Apps band queries the viewer's OWN {owner}/_App records — a GENERIC query, so no
        // app path (marked or not) can ever reach the query string / the `hq=` URL. The marked-app
        // guarantee therefore lives at PAINT (below), where the tile's name would otherwise show.
        var query = UserActivityLayoutAreas.BuildAppsBand(Owner, null).HiddenQuery!.ToString()!;

        query.Should().NotContain("Acme");
        query.Should().Contain($"path:{Owner}/_App");
    }

    [Fact]
    public void AppRecords_AreScreenedByTheirTargetAtPaint()
    {
        // Icons tiles paint straight from the query rows; the view's paint filter is keyed by the
        // tile's navigation TARGET (the record's MainNode), so a marked app hides even though its
        // RECORD path ({owner}/_App/…) is never itself marked.
        var records = new[]
        {
            MeshNode.FromPath($"{Owner}/_App/Acme") with
            {
                NodeType = AppNodeType.NodeType, Name = "Acme", MainNode = "Acme",
            },
            MeshNode.FromPath($"{Owner}/_App/Northwind") with
            {
                NodeType = AppNodeType.NodeType, Name = "Northwind", MainNode = "Northwind",
            },
        };

        Active("Acme").Filter(records, n => n.MainNode).Select(n => n.Name)
            .Should().Equal("Northwind");
        // Mode off (marks curated but not presenting): everything paints.
        PresentationScreen.For(false, ["Acme"]).Filter(records, n => n.MainNode)
            .Should().HaveCount(2);
    }

    // ── The legacy single-list catalog ─────────────────────────────────────────────────────────

    [Fact]
    public void LegacyCatalog_SharedBand_DisappearsWhenEveryTargetIsMarked()
    {
        var catalog = UserActivityLayoutAreas.BuildCatalog(
            Owner, config: null, sharedTargets: ["Acme/Deals"], locale: null, screen: Active("Acme"));

        // No band at all — the catalog is the single list again, exactly as with no grants.
        catalog.Should().BeOfType<MeshSearchControl>();
    }

    [Fact]
    public void LegacyCatalog_SharedBand_IsUnchangedWhenTheModeIsOff()
    {
        var catalog = UserActivityLayoutAreas.BuildCatalog(
            Owner, config: null, sharedTargets: ["Acme/Deals"], locale: null,
            screen: PresentationScreen.For(false, ["Acme"]));

        catalog.Should().BeOfType<StackControl>();
    }

    [Fact]
    public void TheCatalogQueryItselfIsNeverRewritten()
    {
        // 🚨 The Spaces list is filtered where its results are PAINTED, never by narrowing the
        // query: a `-path:Acme` clause would put the marked name straight into the `hq=` URL, and
        // would make the privacy screen a query-engine concern — the first step towards it becoming
        // a second access-control system.
        var marked = UserActivityLayoutAreas.BuildCatalog(Owner, screen: Active("Acme"));
        var plain = UserActivityLayoutAreas.BuildCatalog(Owner);

        QueryOf(marked).Should().Be(QueryOf(plain));
        QueryOf(marked).Should().NotContain("Acme");
    }

    // ── The marking affordance ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NodeMenu_OffersHideForAnUnmarkedNode_AndShowForAMarkedOne()
    {
        var hide = PresentationLayoutArea.GetMenuItem("Acme", Owner, PresentationScreen.Off);
        hide!.Area.Should().Be(PresentationLayoutArea.HideArea);
        hide.LabelKey.Should().Be("menu.hideInPresentation");

        // The mark is visible to the menu even while the mode is OFF — that is how you curate the
        // list before you start presenting.
        var show = PresentationLayoutArea.GetMenuItem(
            "Acme", Owner, PresentationScreen.For(false, ["Acme"]));
        show!.Area.Should().Be(PresentationLayoutArea.ShowArea);
        show.LabelKey.Should().Be("menu.showInPresentation");
    }

    [Fact]
    public void NodeMenu_OffersNothingToAnAnonymousViewer_OrOnTheViewersOwnHome()
    {
        PresentationLayoutArea.GetMenuItem("Acme", null, PresentationScreen.Off)
            .Should().BeNull("there is no profile to write the mark to");
        PresentationLayoutArea.GetMenuItem(Owner, Owner, PresentationScreen.Off)
            .Should().BeNull("hiding your own home would empty the page you are reading");
    }

    [Fact]
    public void NodeMenu_IsNotGatedOnAnyPermissionOfTheTarget()
    {
        // The signature is the assertion: marking edits the VIEWER's profile, so no Permission of
        // the marked node is consulted — unlike Move / Copy / Delete, which all take `perms`.
        typeof(PresentationLayoutArea).GetMethod(nameof(PresentationLayoutArea.GetMenuItem))!
            .GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(Permission));
    }
}
