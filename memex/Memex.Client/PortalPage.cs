namespace Memex.Client;

/// <summary>
/// Shows the hosted MeshWeaver portal in an embedded <see cref="WebView"/>. This is the whole portal
/// (Blazor Server, served over the network) inside the app — a top-level navigation, so the OAuth
/// login works (an iframe would be blocked by the identity provider's X-Frame-Options).
///
/// <para>This is the pragmatic "see the portal in the app" path. The native, in-process portal
/// (MAUI Blazor Hybrid rendering the real MeshWeaver component RCLs bound to mesh streams over the
/// SignalR participant) is the larger Phase-1 step — it needs those RCLs decoupled from Blazor Server.</para>
/// </summary>
public sealed class PortalPage : ContentPage
{
    // TODO: make configurable (atioz / memex / local) — same setting as the SignalR endpoint.
    private const string PortalUrl = "https://memex.meshweaver.cloud";

    public PortalPage()
    {
        Title = "Portal";
        Content = new WebView
        {
            Source = PortalUrl,
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
        };
    }
}
