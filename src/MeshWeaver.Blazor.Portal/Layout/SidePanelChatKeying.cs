using MeshWeaver.AI;

namespace MeshWeaver.Blazor.Portal.Layout;

/// <summary>
/// Pure keying / caching rules for the side-panel chat, extracted from
/// <see cref="PortalLayoutBase"/> so the load-bearing invariant — <b>the side-panel
/// conversation's identity is INDEPENDENT of navigation</b> — is unit-testable
/// without a Blazor render host.
///
/// <para>The side-panel chat is keyed (Blazor <c>@key</c>) and cached on the
/// <b>content path</b> only — NEVER on the navigation context's <c>PrimaryPath</c>.
/// A key that embeds <c>PrimaryPath</c> changes on every navigation, so Blazor tears
/// down and recreates the <c>ThreadChatView</c>, destroying the in-progress
/// conversation (the recurring "lost the thread again" bug). The context-attachment
/// chip is refreshed live inside <c>ThreadChatView</c> via its
/// <c>NavigationService.NavigationContext</c> subscription, so the component never
/// needs rebuilding to reflect a navigation change.</para>
/// </summary>
public static class SidePanelChatKeying
{
    /// <summary>
    /// Stable Blazor <c>@key</c> for the side-panel new-chat composer. It is a constant
    /// — deliberately NOT derived from the navigation context — so navigation does not
    /// recreate the composer and lose what the user has typed/submitted.
    /// </summary>
    public const string NewChatKey = "sidepanel-newchat";

    /// <summary>
    /// Decides whether the cached side-panel chat control must be rebuilt. The ONLY
    /// trigger is a change of the content path (a different opened thread, or
    /// new-chat ⇄ opened-thread). The navigation context is deliberately NOT a
    /// parameter: navigation must never invalidate the cached control.
    /// </summary>
    public static bool ShouldRebuildControl(string? cachedContentPath, string currentContentPath)
        => !string.Equals(cachedContentPath, currentContentPath, StringComparison.Ordinal);

    /// <summary>
    /// Decides whether navigating the MAIN view to a thread node should auto-close the side panel.
    /// The rule "a thread lives in EITHER the main view OR the side panel, never both" must fire
    /// ONLY when the user opens a <b>different</b> thread full-screen than the one already shown in
    /// the side panel. It must NEVER fire when the side-panel chat IS the navigated thread, nor when
    /// the side panel holds a brand-new chat (no content path) — otherwise the side-panel
    /// conversation the user is actively in just vanishes (the recurring "chat disappears" bug).
    /// </summary>
    /// <param name="navNodeType">NodeType of the node the main view navigated to.</param>
    /// <param name="navNodePath">Full path of the node the main view navigated to.</param>
    /// <param name="sidePanelContentPath">The side panel's current content path (empty/null = new chat).</param>
    /// <param name="isSidePanelVisible">Whether the side panel is currently visible.</param>
    public static bool ShouldHideSidePanelOnThreadNavigation(
        string? navNodeType, string? navNodePath, string? sidePanelContentPath, bool isSidePanelVisible)
    {
        // Nothing to hide if the panel is already closed.
        if (!isSidePanelVisible)
            return false;
        // The rule only governs THREADS in the main view.
        if (!ThreadNodeType.IsThreadNodeType(navNodeType))
            return false;
        // A brand-new side-panel chat (no content path) must NEVER be yanked away. This is the
        // submit-in-side-panel flow (submitting sets the content path to the freshly-created thread)
        // and the browse-while-composing flow — the recurring "chat disappears" report.
        if (string.IsNullOrEmpty(sidePanelContentPath))
            return false;
        // The navigated thread IS the one already shown in the side panel — same conversation, keep it.
        if (string.Equals(navNodePath, sidePanelContentPath, StringComparison.OrdinalIgnoreCase))
            return false;
        // A genuinely DIFFERENT thread opened full-screen in the main view → enforce "never both".
        return true;
    }
}
