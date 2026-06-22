using Memex.Client.Services;
using Memex.Client.Voice;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;

namespace Memex.Client;

public static class MauiProgram
{
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

        // The native instance manager (start page) — the user's memex instances (base URL + token).
        builder.Services.AddSingleton<InstanceStore>();

        // 🌐 Local-first mesh. This client hosts its OWN in-process monolith mesh — mesh "local" —
        // with SQLite node storage + file-system content under the app-data directory. It builds its
        // own MeshWeaver service provider (separate from the MAUI DI; the hub is resolved from it), so
        // config + content live as local mesh nodes, fully offline. Remote portals attach later as
        // additional meshes over SignalR (federation), addressed {meshId}/{path}.
        builder.Services.AddSingleton(BuildLocalMesh());

        // On-device voice: mic capture + Whisper (whisper.cpp, runs locally incl. iOS Metal/GPU).
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

    /// <summary>
    /// Builds the in-process local mesh and returns its hub. The mesh owns a dedicated MeshWeaver
    /// service provider — built with <see cref="ServiceProviderExtensions.CreateMeshWeaverServiceProvider"/>
    /// (NOT the default BuildServiceProvider, which skips module setup) — exactly as proven in
    /// SqliteRawBootstrapTest. SQLite + assembly store + content all live under the app-data dir.
    /// </summary>
    private static IMessageHub BuildLocalMesh()
    {
        var appData = FileSystem.AppDataDirectory;

        var meshServices = new ServiceCollection();
        meshServices.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        meshServices.AddLogging();
        meshServices.AddOptions();

        var meshBuilder = new MeshBuilder(
                c => c.Invoke(meshServices),
                AddressExtensions.CreateMeshAddress("local"))
            .UseMonolithMesh()
            .AddPartitionedSqlitePersistence($"Data Source={Path.Combine(appData, "memex-local.db")}")
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .ConfigureServices(s => s.AddFileSystemAssemblyStore(Path.Combine(appData, "assembly-store")));
        meshServices.AddSingleton(meshBuilder.BuildHub);

        var meshSp = meshServices.CreateMeshWeaverServiceProvider();
        var hub = meshSp.GetRequiredService<IMessageHub>();

        // Single device-user identity for every local operation (single-user local mesh).
        hub.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = "device-user", Name = "Device User" });

        return hub;
    }
}
