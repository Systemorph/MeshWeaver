using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Sample test users from samples/Graph/Data/User/.
/// Provides DevLogin-style user setup for tests without external auth providers.
/// </summary>
public static class TestUsers
{
    /// <summary>
    /// Default admin context for tests (DevLogin).
    /// Used by MonolithMeshTestBase.InitializeAsync() to log in a user.
    /// </summary>
    public static readonly AccessContext Admin = new()
    {
        ObjectId = "Roland",
        Name = "Roland",
        Email = "rbuergi@systemorph.com",
        Roles = ["Admin"]
    };

    /// <summary>
    /// Dedicated test user with Admin access.
    /// Has Roland_Access.json and TestUser_Access.json in all sample data folders.
    /// </summary>
    public static readonly AccessContext TestUser = new()
    {
        ObjectId = "TestUser",
        Name = "Test User",
        Email = "testuser@meshweaver.io",
        Roles = ["Admin"]
    };

    /// <summary>
    /// Returns sample user MeshNodes matching samples/Graph/Data/User/*.json.
    /// Requires AddGraph() to register the User and AccessAssignment node types.
    /// </summary>
    public static MeshNode[] SampleUsers() =>
        SampleUserNames
            .SelectMany(name => new MeshNode[]
            {
                // 🚨 The PARTITION-ROOT User node ({name} at namespace="") — the exact shape onboarding
                // (UserOnboardingService.CreateUser) writes. This is what /{name} routing resolves to and
                // what makes the user's partition EXIST + reachable. Without it the user is not "properly
                // onboarded": a thread started from their home has no partition to land in, so routing to
                // {name} fails "No node found" (the not-reachable = wedge symptom). Seeding the catalog
                // entry below WITHOUT this root was the gap ThreadCreatableFromHomeTest pins.
                new(name) { Name = name, NodeType = "User" },
                // The User/{name} catalog / auth-mirror entry (the V27 trigger writes this in prod; seeded
                // here so AgentChatClient.LoadContextNode("User/{name}") resolves).
                new(name, "User") { Name = name, NodeType = "User" },
            })
            .ToArray();

    // TestUser is the default test-circuit identity (TestUsers.TestUser), so it leads the list.
    private static readonly string[] SampleUserNames =
        ["TestUser", "Roland", "Samuel", "Alice", "Bob", "Carol", "David", "Emma"];

    /// <summary>
    /// AccessAssignment granting Public users Admin rights.
    /// Creates one assignment per default partition namespace so that
    /// access rights are visible in each partition's storage.
    /// </summary>
    public static MeshNode[] PublicAdminAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = WellKnownUsers.Public,
            DisplayName = "Public",
            Roles = [new RoleAssignment { Role = "Admin" }]
        };

        // Create an access assignment at each default partition root
        return
        [
            CreateAccessNode("", assignment),    // root level (fallback)
            CreateAccessNode("Admin", assignment),
            CreateAccessNode("User", assignment),
            CreateAccessNode("Portal", assignment),
            CreateAccessNode("Kernel", assignment),
            CreateAccessNode("ACME", assignment),
            CreateAccessNode("FutuRe", assignment),
            CreateAccessNode("Northwind", assignment),
            CreateAccessNode("Cornerstone", assignment),
            CreateAccessNode("MeshWeaver", assignment),
            CreateAccessNode("Doc", assignment),
            CreateAccessNode("Systemorph", assignment),
            CreateAccessNode("Type", assignment),
            CreateAccessNode("VUser", assignment),
        ];
    }

    private static MeshNode CreateAccessNode(string ns, AccessAssignment assignment) =>
        // Root scope assignments live at "_Access" (not "") so SecurityService.Consume
        // recognizes them as scope = "" (global). Empty Namespace would put them at
        // path "Public_Access" with no scope mapping.
        new(WellKnownUsers.Public + "_Access", ns.Length > 0 ? ns + "/_Access" : "_Access")
        {
            NodeType = "AccessAssignment",
            Name = "Public Access",
            Content = assignment,
            MainNode = ns.Length > 0 ? ns : "",
        };

    /// <summary>
    /// Adds sample users and access assignments as pre-seeded MeshNodes.
    /// Chain in ConfigureMesh: base.ConfigureMesh(builder).AddGraph().AddSampleUsers()
    /// </summary>
    public static MeshBuilder AddSampleUsers(this MeshBuilder builder)
        => builder.AddMeshNodes(SampleUsers());

    /// <summary>
    /// Logs in the default admin user (DevLogin) on the given hub.
    /// Called automatically by MonolithMeshTestBase.InitializeAsync().
    /// </summary>
    public static void DevLogin(IMessageHub mesh, AccessContext? user = null)
    {
        var accessService = mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(user ?? Admin);
    }
}
