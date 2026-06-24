using System.Reactive.Linq;
using Autofac.Extensions.DependencyInjection;
using Memex.Client.Services;
using Memex.Client.Voice;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Maui;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Memex.Client.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;

namespace Memex.Client;

public static class MauiProgram
{
    private const string DeviceUserId = "device-user";

    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // Force the Vulkan GPU backend ahead of CPU for Whisper (Intel Arc / discrete GPUs). The iOS
        // device keeps Metal via the base runtime; Android tries Vulkan then CPU by default.
        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
        [
            Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan,
            Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
        ];
#endif
#if MACCATALYST
        // The macOS client (MacCatalyst) has NO Metal — Whisper.net builds whisper.cpp with GGML_METAL=OFF
        // for this TFM — so CoreML (Apple GPU / Neural Engine) is the only GPU path. Gated behind the
        // "apple-gpu" feature flag (off until the CoreML apple image is hosted — see OnDeviceVoice.md).
        // When on, prefer CoreML over CPU; whisper auto-falls back to CPU if the encoder model is absent
        // (the runtime is built WHISPER_COREML_ALLOW_FALLBACK=ON). Must be set before any WhisperFactory.
        if (FeatureFlags.IsAppleGpuEnabled)
        {
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
            [
                Whisper.net.LibraryLoader.RuntimeLibrary.CoreML,
                Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
            ];
        }
#endif
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // 🌐 LOCAL-FIRST: this client hosts its OWN in-process monolith mesh ("local") on SQLite, and
        // renders the MeshWeaver portal directly against it. The mesh needs an Autofac container
        // (ILifetimeScope drives its per-node sub-hubs), so the WHOLE MAUI host runs on Autofac — that
        // way the mesh, the portal Blazor services (PortalApplication/NavigationService/…), and the
        // BlazorWebView components all live in ONE container and resolve each other. (The proven
        // separate-SP bootstrap from SqliteRawBootstrapTest used CreateMeshWeaverServiceProvider, which
        // is exactly this Autofac build — here the MAUI host performs it.)
        builder.ConfigureContainer(new AutofacServiceProviderFactory());

        var appData = FileSystem.AppDataDirectory;

        // Build the local mesh INTO the MAUI service collection (c.Invoke(builder.Services)) so the
        // portal components can resolve the hub + layout client. AddBlazor wires the portal's Blazor
        // service graph; the view packs map layout-area controls to Blazor components.
        var meshBuilder = new MeshBuilder(
                c => c.Invoke(builder.Services),
                AddressExtensions.CreateMeshAddress("local"))
            .UseMonolithMesh()
            .AddPartitionedSqlitePersistence($"Data Source={Path.Combine(appData, "memex-local.db")}")
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            // Connectable-mesh node type; THIS instance is a MemexInstance node too.
            .AddMemexInstanceType()
            // Single-user local mesh: the device-user (the phone's user) owns everything (root Admin).
            .AddMeshNodes(DeviceUserAdminGrant())
            // Content service (file collections) — non-Blazor; the mesh + native UI use it.
            .ConfigureServices(services => services
                .AddContentService())
            // Hub-side: the layout client + the NATIVE MAUI view pack (maps layout-area controls → native
            // Microsoft.Maui.Controls views). Replaces AddBlazor()/AddGraphViews()/AddRadzenDataGrid() — no
            // AspNetCore.App, so the build targets maccatalyst/iOS.
            .ConfigureHub(hub => hub
                .AddMaui()
                .AddMeshTypes())
            .ConfigureServices(s => s.AddFileSystemAssemblyStore(Path.Combine(appData, "assembly-store")));

        builder.Services.AddSingleton(meshBuilder.BuildHub);

        // Device-persisted feature flags (e.g. the macOS "apple-gpu" CoreML path).
        builder.Services.AddSingleton<FeatureFlags>();

        // On-device voice: mic capture + Whisper (whisper.cpp). Runs on the GPU where available —
        // Metal on the iOS device, Vulkan on Windows/Android, CoreML on macOS (the "apple-gpu" flag).
        builder.Services.AddSingleton<IAudioManager>(AudioManager.Current);
        builder.Services.AddSingleton<AudioCaptureService>();
        // Voice model: the small multilingual Base model (English default, other languages from the same
        // download) ships by default; the large Swiss-German fine-tune is opt-in via the "swiss-german"
        // flag (a 547 MB ggml + ~1.2 GB CoreML apple image on macOS). The CoreML apple image for the
        // SELECTED model is provisioned only on macOS when the apple-gpu flag is on (best-effort → CPU).
        var voiceModel = FeatureFlags.IsSwissGermanEnabled ? WhisperModelSize.SwissGerman : WhisperModelSize.Base;
        builder.Services.AddSingleton(_ => new VoiceModelCatalog(
            Path.Combine(FileSystem.AppDataDirectory, "models"), voiceModel,
            provisionAppleImage: OperatingSystem.IsMacCatalyst() && FeatureFlags.IsAppleGpuEnabled));
        // Inference runs on the local mesh's CPU IoPool — off the UI thread, bounded (ControlledIoPooling).
        builder.Services.AddSingleton(sp => new VoiceService(
            sp.GetRequiredService<VoiceModelCatalog>(),
            sp.GetRequiredService<IMessageHub>().ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Compile)
                ?? IoPool.Unbounded));

        // Remote-mesh join: OAuth (WebAuthenticator + PKCE) obtains a token, then ConnectToMesh + a
        // MemexInstance node so the next boot reconnects. See the /connect page.
        builder.Services.AddSingleton<MeshOAuthClient>();
        builder.Services.AddSingleton(sp => new MeshConnector(sp.GetRequiredService<IMessageHub>()));

        // Native shell: the instance manager (add/open memex instances) is the landing page; opening one
        // shows its portal in PortalHostPage. (The in-process local portal renders natively via the
        // MeshWeaver.Maui LayoutAreaView — wired into a page in the next wave.)
        builder.Services.AddSingleton<InstanceStore>();
        builder.Services.AddTransient<InstanceManagerPage>();
        builder.Services.AddTransient<VoicePage>();

        // Minimalistic on-device logging: a size-capped rolling file + in-memory ring buffer — bounded
        // disk + memory, no dependencies, phone-safe. The provider is also a singleton (FileLoggerProvider)
        // so an in-app diagnostics view can read its Recent lines.
        builder.Logging.AddDeviceFileLogger(
            Path.Combine(FileSystem.AppDataDirectory, "logs", "memex.log"));

        // On-device text AI: Apple Intelligence (Foundation Models) on iPhone + Apple-silicon Macs — we
        // ship NO LLM (only the Swiss-German Whisper model). Elsewhere it reports unavailable (the app
        // falls back to the connected mesh). Lean: the OS provides the model. Runs on the IoPool.
        builder.Services.AddSingleton<IOnDeviceChat>(sp => OnDeviceChat.Create(
            sp.GetRequiredService<IMessageHub>().ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Compile)
                ?? IoPool.Unbounded));

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // The hub is now resolved from the shared MAUI/Autofac container. Stamp the device-user identity
        // (single-user local mesh) and seed the first-boot data.
        var hub = app.Services.GetRequiredService<IMessageHub>();
        hub.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = DeviceUserId, Name = "Device User" });
        SeedOwnInstance(hub);

        return app;
    }

    /// <summary>Root-scope Admin grant for the device user — they own the single-user local mesh.</summary>
    private static MeshNode DeviceUserAdminGrant() =>
        // Root scope lives at "_Access" (NOT "") so SecurityService recognises scope = "" (global) —
        // see TestUsers.CreateAccessNode.
        new(DeviceUserId + "_Access", "_Access")
        {
            NodeType = "AccessAssignment",
            Name = "Device User — Admin",
            Content = new AccessAssignment
            {
                AccessObject = DeviceUserId,
                DisplayName = "Device User",
                Roles = [new RoleAssignment { Role = "Admin" }],
            },
            MainNode = "",
        };

    /// <summary>
    /// Seeds the "own instance" MemexInstance node (named after the device) when it's absent — the one
    /// default entry every new app gets, representing the local mesh itself.
    /// </summary>
    private static void SeedOwnInstance(IMessageHub hub)
    {
        var deviceName = DeviceInfo.Current.Name;
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = "My Memex";

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("LocalMeshSeed");

        // Existence check via GetQuery (emits the current set — empty on a fresh mesh — immediately);
        // GetMeshNodeStream(path) instead WAITS for a node to appear, so it can't detect absence.
        hub.GetWorkspace().GetQuery("boot-own-instance", $"nodeType:{MemexInstanceNodeType.NodeType}")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Where(existing => !existing.Any())
            .SelectMany(_ => meshService.CreateNode(new MeshNode("local", MemexInstanceNodeType.Segment)
            {
                NodeType = MemexInstanceNodeType.NodeType,
                Name = deviceName,
                Content = new MemexInstanceContent { DisplayName = deviceName, MeshId = "local" },
            }))
            .Subscribe(
                _ => logger?.LogInformation("Seeded own instance {Name}", deviceName),
                ex => logger?.LogWarning(ex, "Failed to seed own MemexInstance node"));
    }
}
