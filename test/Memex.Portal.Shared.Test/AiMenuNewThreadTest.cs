using MeshWeaver.Blazor.Portal.Layout;
using MeshWeaver.Graph;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the AI (✨) menu's "New thread" entry end to end: that it is seeded at all, that it stays
/// imperative (no Href), and that it routes to the user's OWN composer in the MAIN pane.
/// <para>
/// Why this exists: "New thread" was reported missing from the sparkle menu and there was no test
/// anywhere asserting the AI menu's contents — a dropped seed entry, or an Href accidentally added to
/// it, would both have shipped silently. The live server payload turned out to carry the item, which
/// is exactly why the interesting contract is the SHAPE of the entry, not merely its presence.
/// </para>
/// </summary>
public class AiMenuNewThreadTest
{
    [Fact]
    public void AiMenu_Seeds_NewThread_First()
    {
        var items = MemexConfiguration.AiMenuItems;

        var newThread = Assert.Single(items, i => i.Area == PortalLayoutBase.AiNewThreadAction);
        Assert.Equal("New thread", newThread.Label);

        // Order 0 → it sorts ahead of every other AI entry (Threads 10, Models 20, …). The menu
        // aggregator inserts into an ImmutableSortedSet keyed on Order, so this IS the positioning
        // contract — there is no post-hoc OrderBy to fall back on.
        Assert.Equal(0, newThread.Order);
        Assert.All(
            items.Where(i => i.Area != PortalLayoutBase.AiNewThreadAction),
            i => Assert.True(i.Order > 0, $"'{i.Label}' must sort after New thread"));
    }

    [Fact]
    public void NewThread_Icon_Is_LanguageNeutral()
    {
        var newThread = MemexConfiguration.AiMenuItems
            .Single(i => i.Area == PortalLayoutBase.AiNewThreadAction);

        // A plus reads as "new" in every locale. Pin BOTH halves of that contract: the glyph itself,
        // and that IsEmoji routes it to a <span> — an icon that fell through to the IsImageUrl branch
        // would render as <img src="➕"> (a broken-image box), which is how icon regressions show up.
        Assert.Equal("➕", newThread.Icon);
        Assert.True(MeshNodeImageHelper.IsEmoji(newThread.Icon));
        Assert.False(MeshNodeImageHelper.IsImageUrl(newThread.Icon));
    }

    [Fact]
    public void NewThread_Carries_No_Href_So_It_Stays_Imperative()
    {
        var newThread = MemexConfiguration.AiMenuItems
            .Single(i => i.Area == PortalLayoutBase.AiNewThreadAction);

        // The destination (/User/{me}/Chat) depends on the signed-in user, which the static seed cannot
        // know — it is resolved at click time from the circuit. HandleMenuItemClick matches the sentinel
        // FIRST and returns, so an Href here would never be followed: it would be dead code that reads
        // like a working destination. This pins the declaration to the behaviour.
        Assert.Null(newThread.Href);
    }

    [Fact]
    public void NewThread_Routes_To_The_Users_Own_Composer_In_Main()
    {
        var href = PortalLayoutBase.NewThreadHref("alice");

        // /User/{me}/Chat — ChatArea renders ComposerAreaView, i.e. the SAME ThreadChatControl the
        // side panel mounts for a new chat. User-scoped, never scoped to the node being viewed.
        Assert.Equal($"/User/alice/{UserActivityLayoutAreas.ChatArea}", href);
    }

    [Fact]
    public void Every_Other_AiMenu_Entry_Navigates_By_Href()
    {
        // The inverse of the sentinel contract: everything that is NOT the imperative entry must be a
        // plain navigation, so it works identically from any page and needs no client state.
        Assert.All(
            MemexConfiguration.AiMenuItems.Where(i => i.Area != PortalLayoutBase.AiNewThreadAction),
            i => Assert.False(string.IsNullOrEmpty(i.Href), $"'{i.Label}' must navigate by Href"));
    }

    [Fact]
    public void AiMenu_Entries_Are_Uniquely_Keyed()
    {
        // The aggregator dedupes on (Order, Label, Area) via ImmutableSortedSet — two entries
        // colliding on all three would silently drop one from the rendered menu.
        var keys = MemexConfiguration.AiMenuItems.Select(i => (i.Order, i.Label, i.Area)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
