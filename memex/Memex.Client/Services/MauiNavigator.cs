using Memex.Client.Pages;
using MeshWeaver.Maui;
using MeshWeaver.Messaging;

namespace Memex.Client.Services;

/// <summary>
/// Bridges the native view pack's <see cref="IMauiNavigator"/> to the shell's <see cref="NavigationService"/>:
/// a href-style <c>NavLinkControl</c> click navigates the shell to that mesh path by pushing a
/// <see cref="NavLocation"/> that builds a <see cref="NodeAreaView"/> for the node's area — the same
/// destination the shell uses for search results and node cards. Registered into the shared mesh/MAUI
/// container so <c>MauiView</c>s resolve it from <c>Stream.Hub.ServiceProvider</c>.
/// </summary>
public sealed class MauiNavigator(NavigationService nav, IMessageHub hub) : IMauiNavigator
{
    public void NavigateTo(string href, string? title = null)
    {
        var path = Normalize(href);
        if (string.IsNullOrEmpty(path)) return;
        const string area = "Overview";
        var label = string.IsNullOrWhiteSpace(title) ? LastSegment(path) : title!;
        // Same shape PortalShellPage.NavigateToNode uses — node path + Overview area → NodeAreaView.
        nav.Navigate(new NavLocation(label, path, area, () => new NodeAreaView(hub, path, area)));
    }

    // Mesh URL shape is {baseUrl}/{meshpath} — strip a leading '/' and the local-only '@/' (or bare '@')
    // prefix so the remainder is the node path the shell navigates to.
    private static string Normalize(string href)
    {
        var s = href.Trim();
        if (s.StartsWith("@/", StringComparison.Ordinal)) s = s[2..];
        else if (s.StartsWith('@')) s = s[1..];
        return s.TrimStart('/');
    }

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i >= 0 && i < path.Length - 1 ? path[(i + 1)..] : path;
    }
}
