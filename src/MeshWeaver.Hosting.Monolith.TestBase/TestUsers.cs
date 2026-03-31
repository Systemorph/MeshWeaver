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
    [
        new("Roland", "User") { Name = "Roland", NodeType = "User" },
        new("Samuel", "User") { Name = "Samuel", NodeType = "User" },
        new("Alice", "User") { Name = "Alice", NodeType = "User" },
        new("Bob", "User") { Name = "Bob", NodeType = "User" },
        new("Carol", "User") { Name = "Carol", NodeType = "User" },
        new("David", "User") { Name = "David", NodeType = "User" },
        new("Emma", "User") { Name = "Emma", NodeType = "User" },
    ];

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
        new(WellKnownUsers.Public + "_Access", ns.Length > 0 ? ns + "/_Access" : "")
        {
            NodeType = "AccessAssignment",
            Name = "Public Access",
            Content = assignment,
            MainNode = ns.Length > 0 ? ns : null!,
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
