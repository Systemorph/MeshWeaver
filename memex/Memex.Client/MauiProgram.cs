using Memex.Client.Services;
using Memex.Client.Voice;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;

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
            config => config.UseSignalRClient(
                PortalSignalRUrl,
                // Per-user identity: the API token (entered on the Mesh page) is sent on every
                // connect; the server validates it and writes carry the user. Read at connect time.
                accessTokenProvider: () => SecureStorage.Default.GetAsync("mesh.token")));

        // On-device voice: mic capture + Whisper (whisper.cpp, runs locally incl. iOS Metal/GPU).
        // The model downloads to app data on first use. Transcript feeds the mesh participant.
        builder.Services.AddSingleton<IAudioManager>(AudioManager.Current);
        builder.Services.AddSingleton<AudioCaptureService>();
        builder.Services.AddSingleton(_ => new VoiceModelCatalog(
            Path.Combine(FileSystem.AppDataDirectory, "models")));
        builder.Services.AddSingleton(sp => new VoiceService(
            sp.GetRequiredService<VoiceModelCatalog>()));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
