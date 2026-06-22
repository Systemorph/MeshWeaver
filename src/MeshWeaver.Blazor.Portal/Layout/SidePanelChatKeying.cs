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
}
