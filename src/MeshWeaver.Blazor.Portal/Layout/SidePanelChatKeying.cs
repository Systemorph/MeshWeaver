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
        // ...even when the two representations differ only by PREFIX (a "User/"-prefixed nav path vs the
        // bare-partition ContentPath) or a trailing cell segment: compare the stable thread IDENTITY (the
        // slug right after "/_Thread/"). Without this, a background nav-context emission to the panel's
        // OWN thread during execution — NOT the user opening a different thread full-screen — trips the
        // full-path inequality above and vanishes the active side-panel chat (the round-3 vanish that
        // survived the earlier @key / keep-last-good fixes).
        if (SameThreadIdentity(navNodePath, sidePanelContentPath))
            return false;
        // A genuinely DIFFERENT thread opened full-screen in the main view → enforce "never both".
        return true;
    }

    /// <summary>
    /// The thread slug — the segment immediately after <c>/_Thread/</c> — of <paramref name="path"/>,
    /// or null when the path holds no thread. A prefix/suffix-stable identity for a thread, so two
    /// representations of the SAME thread (differing only by a partition prefix or a trailing cell
    /// segment) compare equal.
    /// </summary>
    internal static string? ThreadSlug(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        const string marker = "/_Thread/";
        var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return null;
        var rest = path[(i + marker.Length)..];
        var slash = rest.IndexOf('/');
        return slash >= 0 ? rest[..slash] : rest;
    }

    /// <summary>True when both paths name the SAME thread by <see cref="ThreadSlug"/> identity.</summary>
    private static bool SameThreadIdentity(string? a, string? b)
    {
        var slug = ThreadSlug(a);
        return slug is not null && string.Equals(slug, ThreadSlug(b), StringComparison.OrdinalIgnoreCase);
    }
}
