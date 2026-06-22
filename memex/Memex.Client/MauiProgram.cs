using Memex.Client.Services;
using Memex.Client.Voice;
using MeshWeaver.Connection.SignalR;
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
        var instances = new InstanceStore();
        builder.Services.AddSingleton(instances);

        // 🌐 Local-first mesh. This client hosts its OWN in-process monolith mesh — mesh "local" —
        // with SQLite node storage + file-system content under the app-data directory. It builds its
        // own MeshWeaver service provider (separate from the MAUI DI; the hub is resolved from it), so
        // config + content live as local mesh nodes, fully offline. It ALSO joins every authenticated
        // instance from the list (URL + token) as a SignalR participant — targets come from the list.
        builder.Services.AddSingleton(BuildLocalMesh(instances));

        // On-device voice: mic capture + Whisper (whisper.cpp, runs locally incl. iOS Metal/GPU).
        builder.Services.AddSingleton<IAudioManager>(AudioManager.Current);
        builder.Services.AddSingleton<AudioCaptureService>();
        // Swiss-German fine-tune (Flurin17, converted to GGML) by default — best on de/Swiss-German,
        // and (being a large-v3-turbo fine-tune) still multilingual for the auto-detect fallback.
        builder.Services.AddSingleton(_ => new VoiceModelCatalog(
            Path.Combine(FileSystem.AppDataDirectory, "models"), WhisperModelSize.SwissGerman));
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
    private static IMessageHub BuildLocalMesh(InstanceStore instances)
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

        // 🌐 Join every AUTHENTICATED instance from the list as a SignalR participant — any number of
        // meshes. Once connected, that mesh can address this client (render its areas, run scripts,
        // message it — the control plane). Connection targets come from the instance list, never hardcoded.
        foreach (var inst in instances.Instances.Where(i => i.IsAuthenticated))
        {
            var url = inst.Url.TrimEnd('/') + "/signalr";
            var token = inst.Token;
            meshBuilder = meshBuilder.ConfigureHub(c => c.UseSignalRClient(url, () => Task.FromResult(token)));
        }

        meshServices.AddSingleton(meshBuilder.BuildHub);

        var meshSp = meshServices.CreateMeshWeaverServiceProvider();
        var hub = meshSp.GetRequiredService<IMessageHub>();

        // Single device-user identity for every local operation (single-user local mesh).
        hub.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = "device-user", Name = "Device User" });

        return hub;
    }
}
