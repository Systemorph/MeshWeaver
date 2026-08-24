using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace Memex.LocalMesh;

/// <summary>
/// Device identity + first-boot seeding for the device mesh — the sidecar twin of the MAUI client's
/// boot (MauiProgram + DeviceOnboarding). Onboarding is INTERACTIVE, exactly like MAUI's
/// OnboardingPage: the device user is created by <see cref="Onboard"/> with the profile the person
/// entered (the shell shows the onboarding dialog on first launch, via <c>POST /api/mesh/onboard</c>)
/// — never auto-seeded, which would land them on an empty profile they never set up. The boot pass
/// only stamps the identity, seeds the <c>MemexInstance</c> nodes (this mesh IS the instance store
/// the shells read their mesh list from), and repairs a missing global-admin grant. All idempotent.
/// </summary>
public static class DeviceSeed
{
    public const string DeviceUserId = "device-user";

    /// <summary>
    /// The single device-user identity every token-less connection to this host acts as (wired
    /// into <c>GrpcOptions.AnonymousUser</c> by Program.cs) — the same trust model as the MAUI
    /// client's in-process mesh. Immutable constant (NoStaticState permits these).
    /// </summary>
    public static AccessContext DeviceUser { get; } = new() { ObjectId = DeviceUserId, Name = OsUserName() };

    public static void Seed(IMessageHub hub)
    {
        // Single-user device mesh: the shells this host serves (RN, web, desktop) connect
        // anonymously; identity lives HERE, exactly like the MAUI client's in-process mesh.
        hub.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(DeviceUser);

        SeedInstances(hub);
        SeedDeviceApps(hub);
        RepairAdminGrant(hub);
    }

    private static string OsUserName()
    {
        var osName = Environment.UserName;
        return string.IsNullOrWhiteSpace(osName) ? "Device User" : osName;
    }

    /// <summary>True once the device user's User node exists (first-launch detection).</summary>
    public static IObservable<bool> IsOnboarded(IMessageHub hub) =>
        hub.GetWorkspace()
            .GetQuery("onboard-check", $"nodeType:User content.email:{DeviceUserId}@local limit:1")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Select(existing => existing.Any());

    /// <summary>
    /// Interactive onboarding — the FRAMEWORK path DeviceOnboarding (MAUI) takes: creating the
    /// partition-root <c>User</c> node provisions the <c>device-user</c> partition and grants
    /// self-admin; the <c>Admin/_Access</c> grant then makes the device owner the instance's global
    /// admin. Emits true when it created the user, false when already onboarded.
    /// </summary>
    public static IObservable<bool> Onboard(IMessageHub hub, string? fullName, string? bio, string? role)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var name = string.IsNullOrWhiteSpace(fullName) ? OsUserName() : fullName.Trim();

        return IsOnboarded(hub).SelectMany(onboarded => onboarded
            ? Observable.Return(false)
            // RunAsSystem, never Observable.Using(ImpersonateAsSystem…) — the latter latches the
            // system identity on the subscribing thread (ImpersonationScopeSiteRatchetGuard, #1790).
            : accessService.RunAsSystem(   // a brand-new partition root is owned by nobody yet
                    () => meshService.CreateNode(new MeshNode(DeviceUserId)   // → provisions schema + self-Admin
                    {
                        NodeType = "User",
                        Name = name,
                        State = MeshNodeState.Active,
                        Content = new User
                        {
                            FullName = name,
                            Email = $"{DeviceUserId}@local",
                            Bio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim(),
                            Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim(),
                        },
                    }))
                .SelectMany(_ => accessService.RunAsSystem(
                    () => meshService.CreateNode(AdminGrant(name))))
                .Select(_ => true));
    }

    /// <summary>Global admin of this instance — Admin/_Access with MainNode="Admin" (the sanctioned
    /// shape; an empty MainNode would be a root/data-superuser grant and is refused).</summary>
    private static MeshNode AdminGrant(string name) => new($"{DeviceUserId}_Access", "Admin/_Access")
    {
        NodeType = "AccessAssignment",
        Name = $"{name} — Admin",
        MainNode = "Admin",
        Content = new AccessAssignment
        {
            AccessObject = DeviceUserId,
            DisplayName = name,
            Roles = [new RoleAssignment { Role = "Admin" }],
        },
    };

    /// <summary>
    /// Heals a crash between Onboard's two creates: the device user exists but the global-admin
    /// grant is missing — on a single-user device mesh the owner must ALWAYS hold it.
    /// </summary>
    private static void RepairAdminGrant(IMessageHub hub)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(nameof(DeviceSeed));

        hub.GetWorkspace()
            .GetQuery("repair-grant-user", $"nodeType:User content.email:{DeviceUserId}@local limit:1")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Where(users => users.Any())
            // The grant carries the ONBOARDED profile name (the user node's), not the OS account —
            // they differ whenever the person entered their real name in the onboarding dialog.
            .SelectMany(users => hub.GetWorkspace()
                .GetQuery("seed-admin-grant", $"path:Admin/_Access/{DeviceUserId}_Access limit:1")
                .Take(1).Timeout(TimeSpan.FromSeconds(15))
                .Where(existing => !existing.Any())
                .Select(_ => users.First().Name ?? OsUserName()))
            .SelectMany(name => accessService.RunAsSystem(
                () => meshService.CreateNode(AdminGrant(name))))
            .Subscribe(
                _ => logger?.LogInformation("Repaired the missing global-admin grant"),
                ex => logger?.LogWarning(ex, "Admin-grant repair failed"));
    }

    /// <summary>
    /// The DEVICE's own system apps, beyond the platform default trio (Store / Documentation /
    /// Threads, which <c>EnsureDefaultApps</c> materializes from <c>Admin/HomeConfig</c> for an
    /// empty grid): <b>Hosting</b> — the deployments-as-data group this mesh carries — belongs on
    /// the Apps grid like any other app, not as a loose card in the content list. The grid paints
    /// ONLY from <c>{owner}/_App</c> records, and an existing device user's grid is not empty, so
    /// the core bootstrap never fires again for them — the device seeds its record here.
    /// Create-if-absent per app (like <see cref="RepairAdminGrant"/>, and unlike the instances
    /// seed): these are the device's SYSTEM apps, kept present by boot on purpose.
    /// </summary>
    private static void SeedDeviceApps(IMessageHub hub)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(nameof(DeviceSeed));
        var path = AppNodeType.PathFor(DeviceUserId, "Hosting");
        hub.GetWorkspace()
            .GetQuery("seed-device-apps", $"path:{path}")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Where(existing => !existing.Any())
            .SelectMany(_ => meshService.CreateNode(
                new MeshNode("Hosting", $"{DeviceUserId}/{AppNodeType.UserNamespace}")
                {
                    NodeType = AppNodeType.NodeType,
                    Name = "Hosting",
                    Icon = "/static/NodeTypeIcons/cloudarrowup.svg",
                    MainNode = "Hosting",
                    State = MeshNodeState.Active,
                    Content = new App { Plugin = "Hosting", Source = "default" },
                }))
            .Subscribe(
                _ => logger?.LogInformation("Seeded the Hosting app record at {Path}", path),
                ex => logger?.LogWarning(ex, "Device app seed failed at {Path}", path));
    }


    /// <summary>
    /// Seeds the instance list on first boot: the own-instance node (this mesh, named after the
    /// machine) plus the public memex — the same defaults the MAUI InstanceStore ships with. The
    /// shells read these (<c>nodeType:MemexInstance</c>) as their connect list. Guarded on "ANY
    /// MemexInstance exists" — deliberately, so a seeded instance the user REMOVED is not
    /// resurrected on the next boot. The two creates are merged (not chained) so one failing cannot
    /// skip the other.
    /// </summary>
    private static void SeedInstances(IMessageHub hub)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(nameof(DeviceSeed));
        var deviceName = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = "My Memex";

        hub.GetWorkspace()
            .GetQuery("seed-instances", $"nodeType:{MemexInstanceNodeType.NodeType}")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Where(existing => !existing.Any())
            .SelectMany(_ => meshService.CreateNode(new MeshNode("local", MemexInstanceNodeType.Segment)
                {
                    NodeType = MemexInstanceNodeType.NodeType,
                    Name = deviceName,
                    Content = new MemexInstanceContent { DisplayName = deviceName, MeshId = "local" },
                })
                // Keyed by URL host — the id MeshConnector (MAUI) and the RN store both derive, so a
                // later token save PATCHES this node instead of minting a duplicate.
                .Merge(meshService.CreateNode(new MeshNode("memex.meshweaver.cloud", MemexInstanceNodeType.Segment)
                {
                    NodeType = MemexInstanceNodeType.NodeType,
                    Name = "memex",
                    Content = new MemexInstanceContent
                    {
                        DisplayName = "memex",
                        Url = "https://memex.meshweaver.cloud",
                        MeshId = "memex.meshweaver.cloud",
                    },
                })))
            .Subscribe(
                _ => logger?.LogInformation("Seeded instance nodes ({Device} + memex)", deviceName),
                ex => logger?.LogWarning(ex, "Instance seed failed"));
    }
}
