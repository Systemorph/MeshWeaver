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

    // ─── The "chat disappears" bug: OnNavigationContextChanged auto-hiding the side panel ───
    //
    // The side panel hosts the active chat. OnNavigationContextChanged enforces "a thread lives
    // in EITHER the main view OR the side panel, never both" by hiding the panel when navigation
    // lands on a thread node. The bug: it hid on ANY thread navigation while visible — including
    // the thread the side panel IS showing, and brand-new side-panel chats — so the conversation
    // the user is actively in vanished during normal chat → submit → navigate use.

    private const string ThreadType = "Thread";     // ThreadNodeType.NodeType
    private const string NonThreadType = "Markdown";

    [Fact]
    public void DoesNotHide_NewSidePanelChat_WhenNavigatingToAnyThread()
    {
        // Side panel holds a brand-new chat (no content path). The user submits it — which navigates /
        // sets the panel to the freshly-created thread — or simply browses to some thread in the main
        // view. The new-chat composer must NOT be yanked away. (Strong candidate: submit-in-side-panel.)
        Assert.False(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "Acme/_Thread/t1", sidePanelContentPath: null, isSidePanelVisible: true));
        Assert.False(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "Acme/_Thread/t1", sidePanelContentPath: string.Empty, isSidePanelVisible: true));
    }

    [Fact]
    public void DoesNotHide_WhenNavigatedThreadIsTheOneOpenInTheSidePanel()
    {
        // The navigated thread IS the side-panel's current thread (e.g. the just-submitted thread the
        // panel now shows, or expanding the side-panel thread). Same conversation → keep it open.
        Assert.False(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "Acme/_Thread/t1", sidePanelContentPath: "Acme/_Thread/t1", isSidePanelVisible: true));
        // Case-insensitive path match.
        Assert.False(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "Acme/_Thread/T1", sidePanelContentPath: "acme/_thread/t1", isSidePanelVisible: true));
    }

    [Fact]
    public void Hides_WhenOpeningADifferentThreadFullScreen()
    {
        // The side panel shows thread t1; the user opens a DIFFERENT thread t2 full-screen in the main
        // view → enforce "never both": hide the panel. This is the ONLY case that should hide.
        Assert.True(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "Acme/_Thread/t2", sidePanelContentPath: "Acme/_Thread/t1", isSidePanelVisible: true));
    }

    [Fact]
    public void DoesNotHide_WhenNavThreadIsThePanelThread_UnderADifferentRepresentation()
    {
        // The nav-context emits the panel's OWN thread with a DIFFERENT representation — a "User/"-
        // prefixed path (main view was /User/Acme), or a trailing cell segment — while ContentPath is
        // the bare thread path. Full-path equality (line 65) misses this and collapses the active chat:
        // the round-3 vanish in SidePanelChatTenMessagesTest, where a BACKGROUND nav emission (NOT a user
        // full-screen open) hit the panel's own thread. Thread-slug identity keeps it open.
        Assert.False(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "User/Acme/_Thread/t1", sidePanelContentPath: "Acme/_Thread/t1", isSidePanelVisible: true));
        Assert.False(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "Acme/_Thread/t1/cell-42", sidePanelContentPath: "Acme/_Thread/t1", isSidePanelVisible: true));
        // A genuinely different slug is still a different thread → still hides (the guard is precise).
        Assert.True(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "User/Acme/_Thread/t2", sidePanelContentPath: "Acme/_Thread/t1", isSidePanelVisible: true));
    }

    [Fact]
    public void DoesNotHide_WhenPanelNotVisible_OrNavigatedNodeIsNotAThread()
    {
        // Panel already hidden → nothing to hide.
        Assert.False(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            ThreadType, "Acme/_Thread/t2", sidePanelContentPath: "Acme/_Thread/t1", isSidePanelVisible: false));
        // Navigated to a non-thread node → the "never both threads" rule doesn't apply.
        Assert.False(SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
            NonThreadType, "Acme/Doc", sidePanelContentPath: "Acme/_Thread/t1", isSidePanelVisible: true));
    }
}
