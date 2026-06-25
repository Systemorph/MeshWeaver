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
            .AddGraph()
            .AddSpaceType()
            // Connectable-mesh node type; THIS instance is a MemexInstance node too.
            .AddMemexInstanceType()
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
        // Auto-onboard the device user with the FRAMEWORK's real onboarding (replaces the old
        // hand-rolled root-Admin seed): on first boot create their proper User node — the framework
        // auto-provisions the per-user partition schema and auto-grants Admin in {device-user}/_Access
        // — then grant platform admin properly (Admin/_Access, NOT a root _Access data-superuser grant).
        // Idempotent: a returning boot already has the User node and skips.
        AutoOnboardDeviceUser(hub, DeviceUserId, osName);
        SeedOwnInstance(hub);

        return app;
    }

    /// <summary>
    /// First-boot onboarding for the single local user — the framework path, not a hand-rolled grant.
    /// Creating the partition-root <c>User</c> node (namespace = "") triggers the framework's
    /// <c>OwnsPartitionProvisioningValidator</c> (provisions the <c>device-user</c> SQLite partition
    /// schema before the write) AND the User post-creation handler (auto-grants Admin in
    /// <c>device-user/_Access</c>). We then add the proper platform-admin grant in <c>Admin/_Access</c>
    /// (<c>MainNode=""</c>) so the device owner is a global admin via <c>hub.IsGlobalAdmin()</c> — the
    /// sanctioned shape, never a root <c>_Access</c> grant. All writes run as System because a
    /// brand-new partition root is owned by nobody yet (mirrors <c>UserOnboardingService.CreateUser</c>).
    /// </summary>
    private static void AutoOnboardDeviceUser(IMessageHub hub, string userId, string displayName)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("DeviceOnboarding");

        // Existence check via GetQuery (emits the current set immediately — empty on a fresh mesh);
        // GetMeshNodeStream(path) would WAIT for the node and so can't detect first-boot absence.
        hub.GetWorkspace()
            .GetQuery("boot-device-user", $"nodeType:User content.email:{userId}@local limit:1")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Where(existing => !existing.Any())
            .SelectMany(_ => Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.CreateNode(new MeshNode(userId) // partition root: namespace "" → provisions {userId} schema + self-Admin
                    {
                        NodeType = "User",
                        Name = displayName,
                        State = MeshNodeState.Active,
                        Content = new User { FullName = displayName, Email = $"{userId}@local" },
                    })
                    // Platform admin — the proper global-admin shape (Admin/_Access, MainNode="").
                    .SelectMany(_ => meshService.CreateNode(new MeshNode($"{userId}_Access", "Admin/_Access")
                    {
                        NodeType = "AccessAssignment",
                        Name = $"{displayName} — Admin",
                        MainNode = "",
                        Content = new AccessAssignment
                        {
                            AccessObject = userId,
                            DisplayName = displayName,
                            Roles = [new RoleAssignment { Role = "Admin" }],
                        },
                    }))))
            .Subscribe(
                _ => logger?.LogInformation("Auto-onboarded device user {User}", userId),
                ex => logger?.LogWarning(ex, "Failed to auto-onboard device user {User}", userId));
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
