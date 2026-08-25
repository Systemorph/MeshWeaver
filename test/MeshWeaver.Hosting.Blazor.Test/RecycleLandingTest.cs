using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// The wire + URL contract of Recycle now that it is a PAGE-level ACTION rather than a layout area
/// on the hub it kills (#2084 / #2202).
///
/// <para>Everything asserted here is pure, so it pins the two doors into the same page-level flow —
/// the node menu's action entry and the <c>/{path}/Recycle</c> URL the stale-build banner links to —
/// without standing up a circuit.</para>
/// </summary>
public class RecycleLandingTest
{
    /// <summary>
    /// Recycle's landing lands on the node's DEFAULT page — the same rule the breadcrumbs follow —
    /// never the hardcoded Overview area. For a plugin node the default page is its rendered COVER;
    /// Overview is the generic raw-body dump, and sending a user there read as a broken page
    /// (memex, 2026-08-25: Cancel on OpenStreetMap/Recycle landed on the un-rendered cover HTML).
    /// </summary>
    [Theory]
    [InlineData("OpenStreetMap", "/OpenStreetMap")]
    [InlineData("Edu/Course", "/Edu/Course")]
    [InlineData("/Chess/", "/Chess")]
    public void BothExits_LandOnTheDefaultPage_NeverOverview(string nodePath, string expected)
    {
        var href = RecycleLayoutArea.LandingHref(nodePath);
        Assert.Equal(expected, href);
        Assert.DoesNotContain("Overview", href);
    }

    /// <summary>
    /// The menu entry is an ACTION, not a navigation — the whole point of #2084, and the half of
    /// #2202 that stops the confirmation being hosted on the doomed hub.
    ///
    /// <para>Its <c>Href</c> is the LANDING page rather than the confirmation URL: for an action the
    /// href is where the page ends up, and it doubles as the graceful degradation for a renderer
    /// that does not know the id (it shows the node instead of a dead area URL). <c>Area</c> stays
    /// <c>Recycle</c> because that is the stable key the MenuPresentation catalog matches on.</para>
    /// </summary>
    [Fact]
    public void MenuEntry_IsAnAction_LandingOnTheNodesOwnPage()
    {
        var item = RecycleLayoutArea.GetMenuItem("Edu/Course", Permission.All);

        Assert.NotNull(item);
        Assert.Equal(MenuActions.Recycle, item!.Action);
        Assert.True(item.IsAction);
        Assert.Equal("/Edu/Course", item.Href);
        Assert.Equal("Recycle", item.Area);
        Assert.Equal(Permission.Update, item.RequiredPermission);
        // Localized, both halves — the label key already shipped, the tooltip is the glyph's voice.
        Assert.Equal("menu.recycle", item.LabelKey);
        Assert.Equal("menu.recycleTooltip", item.TooltipKey);
    }

    /// <summary>
    /// Applicability stays in CODE. The MenuPresentation catalog is override-only and nothing
    /// downstream re-checks <c>RequiredPermission</c>, so a reader must never be handed the entry.
    /// </summary>
    [Fact]
    public void MenuEntry_IsWithheldWithoutUpdatePermission()
        => Assert.Null(RecycleLayoutArea.GetMenuItem("Edu/Course", Permission.Read));

    /// <summary>
    /// The URL door: the page shell recognises <c>/{path}/Recycle</c> and resolves the SAME target
    /// the menu action carries, so the banner link, a bookmark and the menu all run one flow.
    /// A bare <c>/Recycle</c> names no node and must not resolve to "the root".
    /// </summary>
    [Theory]
    [InlineData("Edu/Course/Recycle", "Edu/Course")]
    [InlineData("/Edu/Course/Recycle/", "Edu/Course")]
    [InlineData("OpenStreetMap/recycle", "OpenStreetMap")]
    [InlineData("Edu/Course", null)]
    [InlineData("Recycle", null)]
    [InlineData("/Recycle", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RecycleUrl_ResolvesItsTarget(string? relativePath, string? expected)
        => Assert.Equal(expected, RecycleLayoutArea.TryGetTargetFromUrl(relativePath));

    /// <summary>
    /// 🚨 Two entries that differ ONLY in <c>Action</c> must not compare equal. The live menu stream
    /// dedups with this equality (<c>MenuItemsSequenceComparer</c>) before re-rendering the whole
    /// page, so an un-hashed field would let a navigation entry and a command entry alias each
    /// other and freeze the wrong one on screen.
    /// </summary>
    [Fact]
    public void ActionParticipatesInMenuItemEquality()
    {
        var navigation = new NodeMenuItemDefinition("Recycle", "Recycle", Href: "/Edu/Course");
        var command = navigation with { Action = MenuActions.Recycle };

        Assert.NotEqual(navigation, command);
        // Hash must agree with Equals for the EQUAL pair (a hash inequality assertion would be
        // asserting the absence of a legal collision, not the contract).
        var sameCommand = navigation with { Action = MenuActions.Recycle };
        Assert.Equal(command, sameCommand);
        Assert.Equal(command.GetHashCode(), sameCommand.GetHashCode());
    }
}
