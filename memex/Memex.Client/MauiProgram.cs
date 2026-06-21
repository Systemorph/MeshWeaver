using MeshWeaver.Connection.SignalR;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace Memex.Client;

public static class MauiProgram
{
    // The portal's SignalR mesh endpoint. TODO: make configurable (atioz / memex / local).
    private const string PortalSignalRUrl = "https://memex.meshweaver.cloud/signalr";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // SignalR mesh participant: this client joins the mesh with one portal address per instance
        // and connects to the portal's /signalr endpoint. The hub is a lazy singleton — it connects on
        // first injection (e.g. when the Mesh page is opened). Interactive Blazor runs in-process; only
        // mesh data crosses the socket. TODO: stabilise the address id per install (Preferences).
        builder.Services.AddMessageHubs(
            AddressExtensions.CreatePortalAddress("memex-client"),
            config => config.UseSignalRClient(PortalSignalRUrl));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
