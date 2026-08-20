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
/// First-boot seeding for the device mesh — the sidecar twin of the MAUI client's boot
/// (MauiProgram + DeviceOnboarding): stamp the single device-user identity on the host, create the
/// partition-root <c>User</c> node through the FRAMEWORK path (the framework provisions the
/// <c>device-user</c> partition and grants self-admin; the <c>Admin/_Access</c> grant then makes the
/// device owner the instance's global admin), and seed the <c>MemexInstance</c> nodes — this mesh IS
/// the instance store the shells it serves read their mesh list from (no device storage).
/// All idempotent: every seed checks existence via <c>GetQuery</c> first.
/// </summary>
public static class DeviceSeed
{
    public const string DeviceUserId = "device-user";

    public static void Seed(IMessageHub hub)
    {
        var osName = Environment.UserName;
        if (string.IsNullOrWhiteSpace(osName)) osName = "Device User";

        // Single-user device mesh: the shells this host serves (RN, web, desktop) connect
        // anonymously; identity lives HERE, exactly like the MAUI client's in-process mesh.
        var deviceUser = new AccessContext { ObjectId = DeviceUserId, Name = osName };
        hub.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(deviceUser);

        SeedDeviceUser(hub, osName);
        SeedInstances(hub);
    }

    /// <summary>Creates the device user on first boot (absent = never onboarded), as DeviceOnboarding does.</summary>
    private static void SeedDeviceUser(IMessageHub hub, string name)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(nameof(DeviceSeed));

        hub.GetWorkspace()
            .GetQuery("seed-device-user", $"nodeType:User content.email:{DeviceUserId}@local limit:1")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Where(existing => !existing.Any())
            .SelectMany(_ => Observable.Using(
                () => accessService.ImpersonateAsSystem(),   // a brand-new partition root is owned by nobody yet
                _ => meshService.CreateNode(new MeshNode(DeviceUserId)   // partition root → provisions schema + self-Admin
                    {
                        NodeType = "User",
                        Name = name,
                        State = MeshNodeState.Active,
                        Content = new User { FullName = name, Email = $"{DeviceUserId}@local" },
                    })))
            .Subscribe(
                _ => logger?.LogInformation("Seeded device user {Name}", name),
                ex => logger?.LogWarning(ex, "Device-user seed failed"));

        // Global admin of this instance — Admin/_Access with MainNode="Admin" (the sanctioned shape;
        // an empty MainNode would be a root/data-superuser grant and is refused). Guarded by the
        // GRANT's own existence, not the user's: a crash between the two creates heals on the next
        // boot, and on a single-user device mesh the owner must ALWAYS hold this grant.
        hub.GetWorkspace()
            .GetQuery("seed-admin-grant", $"path:Admin/_Access/{DeviceUserId}_Access limit:1")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Where(existing => !existing.Any())
            .SelectMany(_ => Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.CreateNode(new MeshNode($"{DeviceUserId}_Access", "Admin/_Access")
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
                })))
            .Subscribe(
                _ => logger?.LogInformation("Seeded global-admin grant for {Name}", name),
                ex => logger?.LogWarning(ex, "Admin-grant seed failed"));
    }

    /// <summary>
    /// Seeds the instance list on first boot: the own-instance node (this mesh, named after the
    /// machine) plus the public memex — the same defaults the MAUI InstanceStore ships with. The
    /// shells read these (<c>nodeType:MemexInstance</c>) as their connect list.
    /// </summary>
    private static void SeedInstances(IMessageHub hub)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(nameof(DeviceSeed));
        var deviceName = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = "My Memex";

        // Guarded on "ANY MemexInstance exists" — deliberately, so a seeded instance the user
        // REMOVED is not resurrected on the next boot. The two creates below are merged (not
        // chained) so one failing cannot skip the other.
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
