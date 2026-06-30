namespace MeshWeaver.Maui;

/// <summary>
/// Client-side navigation for the native MAUI view pack: the AspNetCore-free counterpart of the Blazor
/// portal's <c>&lt;a href&gt;</c> / <c>INavigationService.NavigateTo</c>. A href-style nav link
/// (<see cref="Layout.NavLinkControl"/> with a <c>Url</c> but no server <c>ClickAction</c>) navigates the
/// shell to that mesh path instead of posting a <c>ClickedEvent</c> — exactly as the portal turns a nav
/// link's URL into a route change.
/// <para>
/// The implementation lives in the MAUI app (it wraps the shell's navigation + builds the node view) and is
/// registered into the SHARED mesh/MAUI container, so <see cref="MauiView"/>s resolve it from
/// <c>Stream.Hub.ServiceProvider</c>. When no navigator is registered (e.g. a host that has no shell), nav
/// links fall back to their <c>ClickAction</c>.
/// </para>
/// </summary>
public interface IMauiNavigator
{
    /// <summary>
    /// Navigates the shell to <paramref name="href"/> — a mesh path in the portal URL shape
    /// (<c>{meshpath}</c>, optionally leading <c>/</c>). The path resolves to a node's area (default
    /// <c>Overview</c>), matching <c>{baseUrl}/{meshpath}</c>.
    /// </summary>
    /// <param name="href">The navigation target (a mesh path; leading <c>/</c> and the local-only <c>@/</c> prefix are tolerated).</param>
    /// <param name="title">Optional display title for the destination; falls back to the path's last segment.</param>
    void NavigateTo(string href, string? title = null);
}
