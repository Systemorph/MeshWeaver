using System.Reactive.Linq;
using Autofac.Extensions.DependencyInjection;
using Memex.Client.Services;
using Memex.Client.Voice;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Layout;
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
using Memex.Client.Prefs;
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
        // for this TFM — so CoreML (Apple GPU / Neural Engine) is the only GPU path, behind the "apple-gpu"
        // flag. That flag defaults OFF: the CoreML encoder triggers a first-run ANE compile that wedges the
        // synchronous model-load on MacCatalyst (see FeatureFlags). So pick the runtime DETERMINISTICALLY —
        // CoreML-first only when the flag is on, otherwise CPU-ONLY so no CoreML load is even attempted (the
        // CoreML runtime package ships the CPU lib too, so [Cpu] resolves fine). Set before any WhisperFactory.
        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = FeatureFlags.IsAppleGpuEnabled
            ?
            [
                Whisper.net.LibraryLoader.RuntimeLibrary.CoreML,
                Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
            ]
            : [Whisper.net.LibraryLoader.RuntimeLibrary.Cpu];
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
            // Sandbox: the real NuGetAssemblyResolver reads NuGet.Config at construction (denied on
            // MacCatalyst/iOS), which crashed per-node hub activation → node-area spinner. Pre-register a
            // no-op (the device never compiles `#r "nuget:"`) BEFORE AddGraph, whose AddNuGetResolver
            // TryAddSingleton then no-ops. See NoOpNuGetAssemblyResolver + the COMPILER_PROBE diagnosis.
            .ConfigureServices(services => services
                .AddSingleton<MeshWeaver.NuGet.INuGetAssemblyResolver, NoOpNuGetAssemblyResolver>())
            .AddGraph()
            .AddSpaceType()
            // Connectable-mesh node type; THIS instance is a MemexInstance node too.
            .AddMemexInstanceType()
            // Layered application preferences (Device → User → Space → System cascade) + UI zoom.
            // Registers the AppPreferences node type, the IPreferencesService singleton, and SettingsPage.
            .AddPreferences()
            // Content service (file collections) — non-Blazor; the mesh + native UI use it.
            .ConfigureServices(services => services
                .AddContentService())
            // Hub-side: the layout client + the NATIVE MAUI view pack (maps layout-area controls → native
            // Microsoft.Maui.Controls views). Replaces AddBlazor()/AddGraphViews()/AddRadzenDataGrid() — no
            // AspNetCore.App, so the build targets maccatalyst/iOS.
            .ConfigureHub(hub => hub
                .AddMaui()
                .AddMeshTypes()
                // The local "home" portal area — an intro + a live DataGrid of the mesh's real nodes,
                // rendered natively by the MAUI view pack from the in-process SQLite mesh (see LocalPortal).
                .AddLayout(layout => layout.WithView("home", LocalPortal.Home)))
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

        // Native browser-like shell: PortalShellPage (back/forward history + instance switcher in the title)
        // hosts content views — the in-process local portal (rendered natively via the MeshWeaver.Maui
        // LayoutAreaView), on-device voice, and the instance manager.
        builder.Services.AddSingleton<InstanceStore>();
        builder.Services.AddSingleton<DeviceOnboarding>();
        // The single source of truth for "where we are" — drives the content frame AND the top-bar menu.
        builder.Services.AddSingleton<NavigationService>();
        builder.Services.AddTransient<OnboardingPage>();
        builder.Services.AddTransient<PortalShellPage>();
        builder.Services.AddTransient<VoiceView>();
        builder.Services.AddTransient<InstanceManagerView>();
        builder.Services.AddTransient<ChatView>();

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
        // (single-user local mesh, no login) and seed the first-boot data. ObjectId stays the stable
        // "device-user" (the root-Admin grant is keyed to it); the display Name takes the OS account name,
        // so greetings/attributions show the real person. Remote instances still authenticate via OAuth.
        var hub = app.Services.GetRequiredService<IMessageHub>();
        var osName = Environment.UserName;
        if (string.IsNullOrWhiteSpace(osName)) osName = "Device User";
        hub.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = DeviceUserId, Name = osName });
        // Onboarding is now INTERACTIVE (OnboardingPage): on first launch the user fills in their full
        // name + bio, and "Get started" runs DeviceOnboarding (creates the User node → framework provisions
        // the user partition + self-admin, then global admin in Admin/_Access). A returning launch detects
        // the existing User node and goes straight to the shell.
        SeedOwnInstance(hub);

        return app;
    }


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
