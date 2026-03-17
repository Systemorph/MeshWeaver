using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests verifying that User node access control is correct:
/// - The User node itself (User/Alice) is publicly readable
/// - Child nodes under a User (User/Alice/SomeThread) are NOT publicly readable
/// - The owner can read their own children (via UserScopeGrantHandler Viewer role)
/// </summary>
public class UserNodeAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Use ConfigureMeshBase (RLS enabled, NO PublicAdminAccess) so access control is real.
    /// Seed test nodes via AddMeshNodes (bypasses RLS validators).
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                // User nodes
                MeshNode.FromPath("User/Alice") with
                {
                    Name = "Alice",
                    NodeType = "User",
                    State = MeshNodeState.Active,
                },
                MeshNode.FromPath("User/Alice/MyThread") with
                {
                    Name = "Alice's Private Thread",
                    NodeType = "Thread",
                    State = MeshNodeState.Active,
                    MainNode = "User/Alice",
                },
                MeshNode.FromPath("User/Bob") with
                {
                    Name = "Bob",
                    NodeType = "User",
                    State = MeshNodeState.Active,
                },
                // Organization (Group) nodes
                new("ACME") { Name = "ACME Corp", NodeType = "Group", State = MeshNodeState.Active },
                MeshNode.FromPath("ACME/Project1") with
                {
                    Name = "Project 1",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                    MainNode = "ACME",
                }
            );

    protected override async Task SetupAccessRightsAsync()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        // Grant Alice Viewer role on her user scope (simulating UserScopeGrantHandler)
        await securityService.AddUserRoleAsync("Alice", Role.Viewer.Id, "User/Alice", "system", Ct);
        // Grant Alice Viewer role on ACME organization
        await securityService.AddUserRoleAsync("Alice", Role.Viewer.Id, "ACME", "system", Ct);
    }

    private async Task<Permission> GetPermissionsAsync(string path, string userId)
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        return await securityService.GetEffectivePermissionsAsync(path, userId, Ct);
    }

    [Fact(Timeout = 10000)]
    public async Task Visitor_CanRead_UserNodeItself()
    {
        // Bob can read User/Alice through the persistence layer's INodeTypeAccessRule (UserAccessRule),
        // which grants public read on User-typed nodes with path == "User/{id}".
        // SecurityService alone doesn't know about INodeTypeAccessRule — it only evaluates
        // AccessAssignment nodes. The persistence layer checks INodeTypeAccessRule first.
        // Verify via IMeshService query which goes through the secure persistence layer.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Bob", Name = "Bob" });

        var result = await MeshQuery.QueryAsync<MeshNode>(
            new MeshQueryRequest { Query = "path:User/Alice" }).FirstOrDefaultAsync(Ct);

        result.Should().NotBeNull(
            "User nodes (User/Alice) should be readable by any authenticated user via INodeTypeAccessRule");
    }

    [Fact(Timeout = 10000)]
    public async Task Visitor_CannotRead_UserChildNode()
    {
        // Bob checks permissions on User/Alice/MyThread (a child node)
        var permissions = await GetPermissionsAsync("User/Alice/MyThread", "Bob");

        permissions.HasFlag(Permission.Read).Should().BeFalse(
            "Child nodes under a User (User/Alice/MyThread) should NOT be publicly readable — " +
            "only the User node itself is public, children require explicit access");
    }

    [Fact(Timeout = 10000)]
    public async Task Owner_CanRead_OwnChildNode()
    {
        // Alice checks permissions on her own child node (she has Viewer role on User/Alice)
        var permissions = await GetPermissionsAsync("User/Alice/MyThread", "Alice");

        permissions.HasFlag(Permission.Read).Should().BeTrue(
            "The User node owner should be able to read their own child nodes (via Viewer role on User/Alice scope)");
    }

    [Fact(Timeout = 10000)]
    public async Task Visitor_CannotUpdate_UserNode()
    {
        // Bob checks update permissions on User/Alice
        var permissions = await GetPermissionsAsync("User/Alice", "Bob");

        permissions.HasFlag(Permission.Update).Should().BeFalse(
            "Visitors should not have update permissions on someone else's User node");
    }

    [Fact(Timeout = 10000)]
    public async Task Owner_CanRead_OwnUserNode()
    {
        // Alice checks permissions on her own User node
        var permissions = await GetPermissionsAsync("User/Alice", "Alice");

        permissions.HasFlag(Permission.Read).Should().BeTrue(
            "The owner should be able to read their own User node");
    }

    // === Organization (Group) access tests ===

    [Fact(Timeout = 10000)]
    public async Task Visitor_CannotRead_Organization()
    {
        // Bob has no access assignment on ACME
        var permissions = await GetPermissionsAsync("ACME", "Bob");

        permissions.HasFlag(Permission.Read).Should().BeFalse(
            "Users without explicit access should NOT be able to read an organization (Group node)");
    }

    [Fact(Timeout = 10000)]
    public async Task Visitor_CannotRead_OrganizationChild()
    {
        // Bob has no access to ACME or its children
        var permissions = await GetPermissionsAsync("ACME/Project1", "Bob");

        permissions.HasFlag(Permission.Read).Should().BeFalse(
            "Users without explicit access should NOT be able to read organization child nodes");
    }

    [Fact(Timeout = 10000)]
    public async Task Member_CanRead_Organization()
    {
        // Alice has Viewer role on ACME
        var permissions = await GetPermissionsAsync("ACME", "Alice");

        permissions.HasFlag(Permission.Read).Should().BeTrue(
            "Users with explicit Viewer access should be able to read the organization");
    }

    [Fact(Timeout = 10000)]
    public async Task Member_CanRead_OrganizationChild()
    {
        // Alice has Viewer role on ACME — should inherit to children
        var permissions = await GetPermissionsAsync("ACME/Project1", "Alice");

        permissions.HasFlag(Permission.Read).Should().BeTrue(
            "Users with Viewer access on an organization should be able to read its child nodes (inherited)");
    }
}
