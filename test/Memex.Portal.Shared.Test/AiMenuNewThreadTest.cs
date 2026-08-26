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
        //
        // 🚨 This is the portal's HALF of the ordering contract. The navigation entries are seeded
        // UiContribution nodes contributed by the MeshWeaver.AI module; their complement (every seed
        // is Order > 0, so all of them sort behind this one) is pinned by
        // MeshWeaver.AI.Test.AiMenuContributionsTest. Disjoint Order ranges are what let the two
        // halves add up to the whole contract without either test referencing the other's assembly.
        Assert.Equal(0, newThread.Order);
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
    public void Only_The_Imperative_Entry_Stays_Compiled()
    {
        // The compiled list holds exactly ONE entry, and it is the imperative sentinel. Everything
        // that is merely a navigation is contributed as DATA (seeded UiContribution nodes from the
        // MeshWeaver.AI module) precisely because it carries no behavior — so a second compiled entry
        // appearing here means someone expressed a link as code when the contribution lane would do.
        //
        // 🚨 The seeds' own contract (Href + LabelKey present, unique keys, Order > 0) is pinned by
        // MeshWeaver.AI.Test.AiMenuContributionsTest. Asserting it here would reintroduce a
        // compile-time dependency from the portal onto the module.
        var only = Assert.Single(MemexConfiguration.AiMenuItems);
        Assert.Equal(PortalLayoutBase.AiNewThreadAction, only.Area);
    }
}
