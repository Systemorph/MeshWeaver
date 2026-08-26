using MeshWeaver.AI.Portal;
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
        // contract — there is no post-hoc OrderBy to fall back on. The navigation entries live in
        // AiMenuContributions.Seeds now (WS7 slice 3), so the ordering contract spans both lists.
        Assert.Equal(0, newThread.Order);
        Assert.All(
            AiMenuContributions.Seeds,
            seed => Assert.True(
                Assert.IsType<MeshWeaver.Graph.Configuration.UiContribution>(seed.Content).Order > 0,
                $"'{seed.Name}' must sort after New thread"));
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
        // The inverse of the sentinel contract: everything that is NOT the imperative entry must be
        // a plain navigation, so it works identically from any page and needs no client state.
        // Those entries are seeded UiContribution nodes (WS7 slice 3) — data, because they carry no
        // behavior; only the imperative "New thread" stays in the compiled list.
        Assert.Single(MemexConfiguration.AiMenuItems);
        Assert.NotEmpty(AiMenuContributions.Seeds);
        Assert.All(AiMenuContributions.Seeds, seed =>
        {
            var contribution = Assert.IsType<MeshWeaver.Graph.Configuration.UiContribution>(seed.Content);
            Assert.Equal("AI", contribution.Context);
            Assert.False(string.IsNullOrEmpty(contribution.Href), $"'{seed.Name}' must navigate by Href");
            Assert.False(string.IsNullOrEmpty(contribution.LabelKey), $"'{seed.Name}' must localize its label");
        });
    }

    [Fact]
    public void AiMenu_Entries_Are_Uniquely_Keyed()
    {
        // The aggregator dedupes on (Order, Label, Area) via ImmutableSortedSet — two entries
        // colliding on all three would silently drop one from the rendered menu. Spans the compiled
        // imperative entry AND the seeded navigation contributions.
        var keys = MemexConfiguration.AiMenuItems
            .Select(i => (i.Order, Label: (string?)i.Label, Area: (string?)i.Area))
            .Concat(AiMenuContributions.Seeds.Select(s =>
            {
                var c = Assert.IsType<MeshWeaver.Graph.Configuration.UiContribution>(s.Content);
                return (c.Order, Label: c.Label, Area: c.Area);
            }))
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
