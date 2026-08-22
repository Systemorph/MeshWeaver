using System.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Presentation mode (issue #1803) on the SURFACES the issue names — the home's tabs (Shared with
/// me, Pinned, Apps and the Spaces catalog, which is where the former Spaces / Last Read / Last
/// Edited tabs live) and the node menu that marks them.
///
/// <para>Three of the tabs filter at the point the QUERY is built rather than only where a card is
/// painted, and that is the interesting part: a pinned path, a shared-band target and an installed
/// app path are all INTERPOLATED INTO the control's query string, which the view exposes in its
/// search-options editor and carries in the <c>hq=</c> parameter of "open in search". A marked name
/// reaching the address bar mid-presentation is the leak, whether or not a card for it is ever
/// drawn.</para>
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

    // ── The scoped home: Shared with me, Pinned, Apps ──────────────────────────────────────────

    /// <summary>A config with no ~/ entry, so BuildHome returns the bare scoped search control.</summary>
    private static HomeConfig NoDock(params string[] apps) =>
        new() { DefaultApps = apps.Length > 0 ? apps : ["Store", "Doc"] };

    private static MeshSearchControl HomeSearch(UiControl home) =>
        home.Should().BeOfType<MeshSearchControl>().Subject;

    private static string[] ScopeLabels(UiControl home) =>
        HomeSearch(home).ScopeTabs!.Select(t => t.Label).ToArray();

    private static string ScopeQuery(UiControl home, string label) =>
        HomeSearch(home).ScopeTabs!.Single(t => t.Label == label).Query;

    [Fact]
    public void Home_AScopeWhoseWholeContentIsMarked_Disappears()
    {
        var user = new User { PinnedPaths = ["Acme/Q3-Renewal"] };

        // Off: both viewer-scoped tabs are there.
        ScopeLabels(UserActivityLayoutAreas.BuildHome(
                Owner, NoDock(), sharedTargets: ["Acme/Deals"], user: user))
            .Should().Equal("Shared with me", "Pinned", "Apps", "Spaces", "All");

        // On, with Acme marked: everything in both of them is inside the marked space, so both tabs
        // go — an empty tab labelled "Pinned" would itself say something.
        ScopeLabels(UserActivityLayoutAreas.BuildHome(
                Owner, NoDock(), sharedTargets: ["Acme/Deals"], user: user, screen: Active("Acme")))
            .Should().Equal("Apps", "Spaces", "All");
    }

    [Fact]
    public void Home_AScopeKeepsItsUnmarkedContent()
    {
        var home = UserActivityLayoutAreas.BuildHome(
            Owner, NoDock(),
            sharedTargets: ["Acme/Deals", "Northwind/Sales"],
            user: new User { PinnedPaths = ["Acme", "Doc/Guide"] },
            screen: Active("Acme"));

        ScopeLabels(home).Should().Equal("Shared with me", "Pinned", "Apps", "Spaces", "All");
    }

    [Fact]
    public void SharedScope_DropsAMarkedTarget()
    {
        var painted = Active("Acme").Retain(["Acme/Deals", "Northwind/Sales"]);
        painted.Should().Equal("Northwind/Sales");

        var home = UserActivityLayoutAreas.BuildHome(
            Owner, NoDock(), sharedTargets: ["Acme/Deals", "Northwind/Sales"], screen: Active("Acme"));
        ScopeQuery(home, "Shared with me")
            .Should().NotContain("Acme").And.Contain("Northwind/Sales");
    }

    [Fact]
    public void AppsScope_DropsAMarkedApp_BeforeItReachesTheQuery()
    {
        // An installed app's path is interpolated into `path:(…)`, so a marked one must never get
        // that far.
        var home = UserActivityLayoutAreas.BuildHome(
            Owner, NoDock("Store"), installedApps: ["Chess", "Acme"], screen: Active("Acme"));

        var query = ScopeQuery(home, "Apps");
        query.Should().Contain("Store").And.Contain("Chess");
        query.Should().NotContain("Acme");
    }

    [Fact]
    public void AppEntries_LeaveSystemAreasAlone()
    {
        // A "~/" entry is a system AREA, not a mesh path — it names no node, so there is nothing for
        // the screen to match and it must pass through even while the mode is up. Asserted because
        // the code only says it in a comment, and a comment is not a test.
        // Marking the system area TOO (a viewer can type anything into their own profile) must not
        // make it disappear, and marking the only real app must not take the dock with it.
        var (systemAreas, paths) = UserActivityLayoutAreas.AppEntries(
            new HomeConfig { DefaultApps = ["~/Settings"] },
            installedApps: ["Acme"],
            screen: Active("Acme", "~/Settings"));

        systemAreas.Should().Equal("~/Settings");
        paths.Should().BeEmpty("every MESH path was screened away; the system-area dock survives");
        // And the home still renders the dock (a stack above the search), never a query carrying
        // the marked name.
        var home = UserActivityLayoutAreas.BuildHome(
            Owner,
            new HomeConfig { DefaultApps = ["~/Settings"] },
            installedApps: ["Acme"],
            screen: Active("Acme", "~/Settings"));
        home.Should().BeOfType<StackControl>("the dock survives the screen").Subject
            .Areas.Should().HaveCount(2, "dock + the scoped search surface");
    }

    [Fact]
    public void AppEntries_AreUnchangedWhenTheModeIsOff()
    {
        var (_, paths) = UserActivityLayoutAreas.AppEntries(
            new HomeConfig { DefaultApps = ["Store"] },
            installedApps: ["Acme"],
            screen: PresentationScreen.For(false, ["Acme"]));

        paths.Should().Contain("Acme");
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

        // Same for the home's Spaces scope: identical query with and without the screen.
        var spacesMarked = HomeSearch(UserActivityLayoutAreas.BuildHome(Owner, NoDock(), screen: Active("Acme")))
            .ScopeTabs!.Single(t => t.Label == "Spaces").Query;
        var spacesPlain = HomeSearch(UserActivityLayoutAreas.BuildHome(Owner, NoDock()))
            .ScopeTabs!.Single(t => t.Label == "Spaces").Query;
        spacesMarked.Should().Be(spacesPlain).And.NotContain("Acme");
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
