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
        ObjectId = "rbuergi@systemorph.com",
        Name = "Roland Bürgi",
        Email = "rbuergi@systemorph.com",
        Roles = ["Admin"]
    };

    /// <summary>
    /// Returns sample user MeshNodes matching samples/Graph/Data/User/*.json.
    /// Requires AddGraph() to register the User and AccessAssignment node types.
    /// </summary>
    public static MeshNode[] SampleUsers() =>
    [
        new("rbuergi@systemorph.com", "User") { Name = "Roland Bürgi", NodeType = "User" },
        new("sglauser@systemorph.com", "User") { Name = "Samuel Glauser", NodeType = "User" },
        new("alice.chen@example.com", "User") { Name = "Alice Chen", NodeType = "User" },
        new("bob.wilson@example.com", "User") { Name = "Bob Wilson", NodeType = "User" },
        new("carol.martinez@example.com", "User") { Name = "Carol Martinez", NodeType = "User" },
        new("david.kim@example.com", "User") { Name = "David Kim", NodeType = "User" },
        new("emma.johnson@example.com", "User") { Name = "Emma Johnson", NodeType = "User" },
    ];

    /// <summary>
    /// AccessAssignment granting Public users Editor rights.
    /// Every authenticated user inherits Public permissions, so this gives
    /// all logged-in users read/write access — suitable for tests.
    /// </summary>
    public static MeshNode PublicEditorAccess() =>
        new(WellKnownUsers.Public + "_Access", "")
        {
            NodeType = "AccessAssignment",
            Name = "Public Access",
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = "Public",
                Roles = [new RoleAssignment { Role = "Editor" }]
            }
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
