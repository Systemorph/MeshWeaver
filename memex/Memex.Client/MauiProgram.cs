using System.Reactive.Linq;
using Autofac.Extensions.DependencyInjection;
using Memex.Client.Services;
using Memex.Client.Voice;
using MeshWeaver.Blazor;
using MeshWeaver.Blazor.Graph;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Radzen;
using MeshWeaver.Blazor.Services;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Plugin.Maui.Audio;

namespace Memex.Client;

public static class MauiProgram
{
    private const string DeviceUserId = "device-user";

    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // Force the Vulkan GPU backend ahead of CPU for Whisper (Intel Arc / discrete GPUs). iOS/macOS
        // keep Metal via the base runtime; Android tries Vulkan then CPU by default.
        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
        [
            Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan,
            Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
        ];
#endif
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

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
            // The portal Blazor component services — the MAUI-safe subset of Hosting.Blazor's AddBlazor
            // (that assembly takes a FrameworkReference to Microsoft.AspNetCore.App, unusable from MAUI).
            // Skipped: the Server-only CircuitHandler/CircuitAccessHandler + AddMeshMcp. NavigationService
            // is replaced by the Hybrid HybridNavigationService (the server one needs NavigationManager
            // from the Server assembly).
            .ConfigureServices(services => services
                .AddContentService()
                .AddFluentUIComponents()
                .AddSingleton<UserIdentityCache>()
                .AddScoped<ICircuitContextAccessor, CircuitContextAccessor>()
                .AddScoped<PortalApplication>()
                .AddScoped<PortalErrorSink>()
                .AddScoped<INavigationService, HybridNavigationService>()
                .AddScoped<IMenuItemsProvider, MenuItemsProvider>())
            // Hub-side: the layout client + view packs (map layout-area controls → Blazor components).
            .ConfigureHub(hub => hub
                .AddBlazor()
                .AddMeshTypes()
                .AddGraphViews()
                .AddRadzenDataGrid())
            .ConfigureServices(s => s.AddFileSystemAssemblyStore(Path.Combine(appData, "assembly-store")));

        builder.Services.AddSingleton(meshBuilder.BuildHub);

        // On-device voice: mic capture + Whisper (whisper.cpp, runs locally incl. iOS Metal/GPU).
        builder.Services.AddSingleton<IAudioManager>(AudioManager.Current);
        builder.Services.AddSingleton<AudioCaptureService>();
        builder.Services.AddSingleton(_ => new VoiceModelCatalog(
            Path.Combine(FileSystem.AppDataDirectory, "models"), WhisperModelSize.SwissGerman));
        // Inference runs on the local mesh's CPU IoPool — off the UI thread, bounded (ControlledIoPooling).
        builder.Services.AddSingleton(sp => new VoiceService(
            sp.GetRequiredService<VoiceModelCatalog>(),
            sp.GetRequiredService<IMessageHub>().ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Compile)
                ?? IoPool.Unbounded));

        // Remote-mesh join: OAuth (WebAuthenticator + PKCE) obtains a token, then ConnectToMesh + a
        // MemexInstance node so the next boot reconnects. See the /connect page.
        builder.Services.AddSingleton<MeshOAuthClient>();
        builder.Services.AddSingleton(sp => new MeshConnector(sp.GetRequiredService<IMessageHub>()));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
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
