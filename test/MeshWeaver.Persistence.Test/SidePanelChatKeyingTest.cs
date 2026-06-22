using MeshWeaver.Blazor.Portal.Layout;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Pins the side-panel chat keying invariant: the side-panel conversation's identity
/// is INDEPENDENT of navigation. The chat is keyed and cached on the (stable) content
/// path only — never on the navigation context's PrimaryPath. Regressing this (re-keying
/// on PrimaryPath) tears down + recreates the ThreadChatView on every navigation and
/// destroys the in-progress conversation — the recurring "lost the thread again" bug.
/// </summary>
public class SidePanelChatKeyingTest
{
    [Fact]
    public void NewChatKey_IsAStableConstant_NotDerivedFromNavigation()
    {
        // A constant — same value regardless of any navigation context. If this were
        // derived from PrimaryPath, navigation would change the @key and recreate the view.
        Assert.Equal("sidepanel-newchat", SidePanelChatKeying.NewChatKey);
    }

    [Fact]
    public void ShouldRebuildControl_DoesNotRebuild_WhenContentPathUnchanged()
    {
        // Same content path → reuse the cached control. There is no PrimaryPath parameter:
        // navigation simply cannot be a rebuild trigger.
        Assert.False(SidePanelChatKeying.ShouldRebuildControl(string.Empty, string.Empty));
        Assert.False(SidePanelChatKeying.ShouldRebuildControl("Acme/_Thread/t1", "Acme/_Thread/t1"));
    }

    [Fact]
    public void ShouldRebuildControl_Rebuilds_WhenContentPathChanges()
    {
        // The ONLY rebuild trigger: opening a different thread, or new-chat ⇄ opened-thread.
        Assert.True(SidePanelChatKeying.ShouldRebuildControl(null, string.Empty));          // first build (no cache yet)
        Assert.True(SidePanelChatKeying.ShouldRebuildControl(string.Empty, "Acme/_Thread/t1")); // new chat → opened thread
        Assert.True(SidePanelChatKeying.ShouldRebuildControl("Acme/_Thread/t1", "Acme/_Thread/t2")); // different thread
        Assert.True(SidePanelChatKeying.ShouldRebuildControl("Acme/_Thread/t1", string.Empty)); // opened thread → new chat
    }
}
