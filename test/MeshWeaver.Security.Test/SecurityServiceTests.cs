using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Documentation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Unit tests for the SecurityService implementation.
/// </summary>
public class SecurityServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder).AddRowLevelSecurity();
    }

    /// <summary>
    /// Skip PublicAdminAccess — security tests need granular permissions.
    /// </summary>
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithAdminRole_ReturnsAllPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user123";
        const string nodePath = "org/acme/project";

        await securityService.AddUserRoleAsync(userId, "Admin", nodePath, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithViewerRole_ReturnsReadOnly()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "viewer123";
        const string nodePath = "org/acme/docs";

        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Execute);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithEditorRole_ReturnsReadCreateUpdate()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "editor123";
        const string nodePath = "org/acme/project/docs";

        await securityService.AddUserRoleAsync(userId, "Editor", nodePath, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_NoRoles_ReturnsNone()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "newuser";
        const string nodePath = "org/private/secure";

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        permissions.Should().Be(Permission.None);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithInheritance_InheritsFromParent()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "inherituser";
        const string parentPath = "org/parent";
        const string childPath = "org/parent/child/grandchild";

        await securityService.AddUserRoleAsync(userId, "Admin", parentPath, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(childPath, userId, TestTimeout);

        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithGlobalRole_AppliesEverywhere()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "globaladmin";
        const string anyPath = "some/random/path";

        await securityService.AddUserRoleAsync(userId, "Admin", null!, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(anyPath, userId, TestTimeout);

        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_CombinesMultipleRoles()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "multiuser";
        const string path1 = "org/project1";
        const string path2 = "org/project1/subproject";

        await securityService.AddUserRoleAsync(userId, "Viewer", path1, "system", TestTimeout);
        await securityService.AddUserRoleAsync(userId, "Editor", path2, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(path2, userId, TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute);
    }

    [Fact(Timeout = 20000)]
    public async Task HasPermission_WithSufficientPermissions_ReturnsTrue()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "readuser";
        const string nodePath = "org/docs/readme";

        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        var canRead = await securityService.HasPermissionAsync(nodePath, userId, Permission.Read, TestTimeout);
        canRead.Should().BeTrue();
    }

    [Fact(Timeout = 20000)]
    public async Task HasPermission_WithoutSufficientPermissions_ReturnsFalse()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "readonlyuser";
        const string nodePath = "org/restricted/data";

        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        var canDelete = await securityService.HasPermissionAsync(nodePath, userId, Permission.Delete, TestTimeout);
        canDelete.Should().BeFalse();
    }

    [Fact(Timeout = 20000)]
    public async Task AddUserRole_CreatesAssignment()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "newassignee";
        const string targetNamespace = "org/newproject";

        await securityService.AddUserRoleAsync(userId, "Editor", targetNamespace, "admin", TestTimeout);

        // Verify via permission check
        var permissions = await securityService.GetEffectivePermissionsAsync(targetNamespace, userId, TestTimeout);
        permissions.Should().HaveFlag(Permission.Update);
    }

    [Fact(Timeout = 20000)]
    public async Task RemoveUserRole_RemovesAssignment()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "removetest";
        const string targetNamespace = "org/removeproject";

        await securityService.AddUserRoleAsync(userId, "Admin", targetNamespace, "admin", TestTimeout);
        await securityService.RemoveUserRoleAsync(userId, "Admin", targetNamespace, TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(targetNamespace, userId, TestTimeout);
        permissions.Should().Be(Permission.None);
    }

    [Fact(Timeout = 20000)]
    public async Task AnonymousUser_GetsAnonymousPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string targetNamespace = "org/public/area";

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", targetNamespace, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(targetNamespace, "", TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Execute);
    }

    [Fact(Timeout = 20000)]
    public async Task GetRole_ReturnsBuiltInRole()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var adminRole = await securityService.GetRoleAsync("Admin", TestTimeout);

        adminRole.Should().NotBeNull();
        adminRole!.Permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task SaveRole_PersistsCustomRole()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var customRole = new Role
        {
            Id = "Contributor",
            DisplayName = "Contributor",
            Permissions = Permission.Read | Permission.Create,
            IsInheritable = true
        };

        await securityService.SaveRoleAsync(customRole, TestTimeout);

        var retrievedRole = await securityService.GetRoleAsync("Contributor", TestTimeout);
        retrievedRole.Should().NotBeNull();
        retrievedRole!.Permissions.Should().Be(Permission.Read | Permission.Create);
    }

    [Fact(Timeout = 20000)]
    public async Task GetRoles_ReturnsAllRoles()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        await securityService.SaveRoleAsync(new Role
        {
            Id = "Auditor",
            Permissions = Permission.Read
        }, TestTimeout);

        var roles = await securityService.GetRolesAsync(TestTimeout).ToListAsync();

        roles.Should().Contain(r => r.Id == "Admin");
        roles.Should().Contain(r => r.Id == "Editor");
        roles.Should().Contain(r => r.Id == "Viewer");
        roles.Should().Contain(r => r.Id == "Auditor");
    }
}

/// <summary>
/// Tests for the RlsNodeValidator integration.
/// </summary>
public class RlsNodeValidatorTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder).AddRowLevelSecurity();
    }

    [Fact(Timeout = 20000)]
    public async Task ValidateAsync_WithoutPermission_ReturnsUnauthorized()
    {
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = new MeshNode("test", "restricted/area") { Name = "Test Node" },
            AccessContext = new AccessContext { ObjectId = "unauthorized-user" }
        };

        var result = await validator!.ValidateAsync(context, TestTimeout);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.Unauthorized);
    }

    [Fact(Timeout = 20000)]
    public async Task ValidateAsync_WithPermission_ReturnsValid()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        const string userId = "authorized-user";

        await securityService.AddUserRoleAsync(userId, "Admin", "allowed/area", "system", TestTimeout);

        var node = new MeshNode("test", "allowed/area") { Name = "Test Node" };
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = node,
            Request = new CreateNodeRequest(node) { CreatedBy = userId },
            AccessContext = new AccessContext { ObjectId = userId }
        };

        var result = await validator!.ValidateAsync(context, TestTimeout);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// Self-access rule: when MainNode matches userId, all operations are allowed
    /// even without explicit permission grants.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ValidateAsync_SelfAccess_MainNodeMatchesUserId_ReturnsValid()
    {
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        const string hubIdentity = "MyHub";
        var node = new MeshNode("MyHub") { Name = "My Hub Node", MainNode = "MyHub" };

        foreach (var op in new[] { NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete })
        {
            var context = new NodeValidationContext
            {
                Operation = op,
                Node = node,
                AccessContext = new AccessContext { ObjectId = hubIdentity }
            };

            var result = await validator!.ValidateAsync(context, TestTimeout);
            result.IsValid.Should().BeTrue($"hub should have {op} access to its own node (MainNode == userId)");
        }
    }

    /// <summary>
    /// Self-access rule should NOT apply when MainNode does not match userId.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ValidateAsync_SelfAccess_MainNodeMismatch_ChecksPermissions()
    {
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        var node = new MeshNode("child", "SomeParent") { Name = "Child Node", MainNode = "SomeParent/child" };
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = node,
            AccessContext = new AccessContext { ObjectId = "different-user" }
        };

        var result = await validator!.ValidateAsync(context, TestTimeout);
        result.IsValid.Should().BeFalse("user should NOT have self-access when MainNode != userId");
    }

    [Fact(Timeout = 20000)]
    public void SupportedOperations_ReturnsCreateUpdateDeleteOperations()
    {
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        var operations = validator!.SupportedOperations;

        operations.Should().Contain(NodeOperation.Read);
        operations.Should().Contain(NodeOperation.Create);
        operations.Should().Contain(NodeOperation.Update);
        operations.Should().Contain(NodeOperation.Delete);
    }
}

/// <summary>
/// Integration tests for hub self-access: a hub can always read/write its own nodes.
/// Nodes are created via persistence (not AddMeshNodes) so they go through
/// the security-aware InMemoryMeshQuery, not the unfiltered StaticNodeQueryProvider.
/// </summary>
public class HubSelfAccessTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity();
    }

    /// <summary>
    /// Skip PublicAdminAccess — we want strict RLS with no global grants.
    /// </summary>
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// Seed the TestHub node via IMeshService so it's stored in persistence
    /// (served by InMemoryMeshQuery with security filtering, not StaticNodeQueryProvider).
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Admin context is set by base.InitializeAsync (DevLogin) — create node via public API.
        // Self-access rule allows this: MainNode="TestHub" matches admin identity for the mesh hub.
        // Grant admin permission so CreateNodeAsync doesn't fail on the RLS check.
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Roland", "Admin", "", "system", TestTimeout);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("TestHub") { Name = "Test Hub" }, TestTimeout);
    }

    /// <summary>
    /// A hub using ImpersonateAsHub can query its own MeshNode
    /// even without any explicit permission grants.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Hub_CanQueryOwnNode_WithImpersonateAsHub()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Create a hub with address "TestHub"
        var hub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("TestHub"),
            c => c);

        MeshNode? node;
        using (accessService.ImpersonateAsHub(hub))
        {
            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            node = await meshService.QueryAsync<MeshNode>("path:TestHub").FirstOrDefaultAsync();
        }

        node.Should().NotBeNull("hub should always be able to see its own node");
        node!.Id.Should().Be("TestHub");
    }

    /// <summary>
    /// Without ImpersonateAsHub and without permissions, a random user
    /// should NOT be able to see the hub's node.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task UnauthorizedUser_CannotQueryHubNode()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Set context to an unauthorized user
        accessService.SetContext(new AccessContext { ObjectId = "random-user", Name = "Random" });
        accessService.SetCircuitContext(new AccessContext { ObjectId = "random-user", Name = "Random" });

        try
        {
            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var node = await meshService.QueryAsync<MeshNode>("path:TestHub").FirstOrDefaultAsync();
            node.Should().BeNull("unauthorized user should not see the hub's node without permissions");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }
}

/// <summary>
/// Tests for security using the sample Graph data (samples/Graph/Data).
/// Tests real-world scenarios with existing access configuration files.
/// Note: Sample data uses AccessAssignment MeshNodes for permission storage.
/// </summary>
public class SampleDataSecurityTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddMeshWeaverDocs()
            .AddAcme()
            .AddDoc()
            .AddRowLevelSecurity();
    }

    [Fact(Timeout = 20000)]
    public async Task Roland_WithGlobalAdminRole_CanEditArchitectureNode()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Roland";
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);
        var canEdit = await securityService.HasPermissionAsync(nodePath, userId, Permission.Update, TestTimeout);

        permissions.Should().Be(Permission.All, "Roland has global Admin role");
        canEdit.Should().BeTrue("Roland should be able to edit the Architecture node");
    }

    [Fact(Timeout = 20000)]
    public async Task Roland_GlobalAdmin_CanEditAnyNode()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Roland";

        var paths = new[]
        {
            "MeshWeaver/Documentation/Architecture",
            "Systemorph",
            "ACME",
            "some/random/path"
        };

        foreach (var path in paths)
        {
            var canEdit = await securityService.HasPermissionAsync(path, userId, Permission.Update, TestTimeout);
            canEdit.Should().BeTrue($"Roland should be able to edit '{path}' as global Admin");
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Alice_WithAcmeEditorRole_CanEditInAcmeOnly()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Alice";

        var canEditAcme = await securityService.HasPermissionAsync("ACME/Project/Task1", userId, Permission.Update, TestTimeout);
        var canEditMeshWeaver = await securityService.HasPermissionAsync("MeshWeaver/Documentation", userId, Permission.Update, TestTimeout);

        canEditAcme.Should().BeTrue("Alice should be able to edit in Software namespace");
        canEditMeshWeaver.Should().BeFalse("Alice should NOT be able to edit in MeshWeaver namespace");
    }

    [Fact(Timeout = 20000)]
    public async Task PublicUser_WithMeshWeaverViewerRole_CannotEdit()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Public";
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        var canEdit = await securityService.HasPermissionAsync(nodePath, userId, Permission.Update, TestTimeout);
        var canRead = await securityService.HasPermissionAsync(nodePath, userId, Permission.Read, TestTimeout);

        canRead.Should().BeTrue("Public user should be able to read MeshWeaver content");
        canEdit.Should().BeFalse("Public user should NOT be able to edit MeshWeaver content");
    }

    [Fact(Timeout = 20000)]
    public async Task Roland_AdminCappedByDocPolicy_ReadOnlyOnDocs()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Roland";
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        // Verify Roland has full access before policy
        var canEditBefore = await securityService.HasPermissionAsync(nodePath, userId, Permission.Update, TestTimeout);
        canEditBefore.Should().BeTrue("Roland should be able to edit before policy is set");

        // Set read-only policy on Documentation
        await securityService.SetPolicyAsync("MeshWeaver/Documentation",
            new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false }, TestTimeout);

        var canEdit = await securityService.HasPermissionAsync(nodePath, userId, Permission.Update, TestTimeout);
        var canRead = await securityService.HasPermissionAsync(nodePath, userId, Permission.Read, TestTimeout);

        canRead.Should().BeTrue("Roland should still be able to read Documentation");
        canEdit.Should().BeFalse("Roland should NOT be able to edit Documentation when policy is active (policy caps to Read + Execute)");

        // Cleanup
        await securityService.RemovePolicyAsync("MeshWeaver/Documentation", TestTimeout);
    }
}

/// <summary>
/// Tests for PartitionAccessPolicy feature.
/// </summary>
public class PartitionAccessPolicyTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder).AddRowLevelSecurity();
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyCapsPermissions_EditorCappedToRead()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "editor1";
        const string ns = "org/docs";

        await securityService.AddUserRoleAsync(userId, "Editor", ns, "system", TestTimeout);
        await securityService.SetPolicyAsync(ns, new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false }, TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(ns, userId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute);
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyCapsAdmin_GlobalAdminCappedToRead()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "admin1";
        const string globalNs = "";
        const string docNs = "platform/docs";

        await securityService.AddUserRoleAsync(userId, "Admin", globalNs, "system", TestTimeout);
        await securityService.SetPolicyAsync(docNs, new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false }, TestTimeout);

        // At the policy namespace, admin should only have Read + Execute
        var docPermissions = await securityService.GetEffectivePermissionsAsync(docNs, userId, TestTimeout);
        docPermissions.Should().Be(Permission.Read | Permission.Execute);

        // At a child path, admin should also only have Read + Execute (policy applies to descendants)
        var childPermissions = await securityService.GetEffectivePermissionsAsync("platform/docs/readme", userId, TestTimeout);
        childPermissions.Should().Be(Permission.Read | Permission.Execute);

        // Outside the policy scope, admin still has full access
        var otherPermissions = await securityService.GetEffectivePermissionsAsync("platform/code", userId, TestTimeout);
        otherPermissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyDoesNotAffectSiblingNamespace()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user2";

        await securityService.AddUserRoleAsync(userId, "Admin", "ACME", "system", TestTimeout);
        await securityService.SetPolicyAsync("Doc", new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false }, TestTimeout);

        var acmePermissions = await securityService.GetEffectivePermissionsAsync("ACME/Project", userId, TestTimeout);
        acmePermissions.Should().Be(Permission.All, "ACME should not be affected by Doc policy");
    }

    [Fact(Timeout = 20000)]
    public async Task NestedPoliciesAccumulate()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user3";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);
        await securityService.SetPolicyAsync("org", new PartitionAccessPolicy { Create = false, Update = false, Delete = false }, TestTimeout);
        await securityService.SetPolicyAsync("org/restricted", new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false }, TestTimeout);

        var orgPermissions = await securityService.GetEffectivePermissionsAsync("org/general", userId, TestTimeout);
        orgPermissions.Should().Be(Permission.Read | Permission.Comment | Permission.Execute, "org level allows Read + Comment + Execute");

        var restrictedPermissions = await securityService.GetEffectivePermissionsAsync("org/restricted/item", userId, TestTimeout);
        restrictedPermissions.Should().Be(Permission.Read | Permission.Execute, "nested policy further restricts to Read + Execute only");
    }

    [Fact(Timeout = 20000)]
    public async Task BreaksInheritance_DiscardsParentRoles()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user4";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);
        await securityService.SetPolicyAsync("isolated",
            new PartitionAccessPolicy { BreaksInheritance = true }, TestTimeout);

        // No local role at "isolated", and inheritance is broken, so no permissions
        var permissions = await securityService.GetEffectivePermissionsAsync("isolated/item", userId, TestTimeout);
        permissions.Should().Be(Permission.None, "inherited Admin from global should be discarded");
    }

    [Fact(Timeout = 20000)]
    public async Task BreaksInheritance_KeepsLocalRoles()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user5";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);
        await securityService.SetPolicyAsync("scoped",
            new PartitionAccessPolicy { BreaksInheritance = true }, TestTimeout);
        await securityService.AddUserRoleAsync(userId, "Editor", "scoped", "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("scoped/item", userId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute,
            "local Editor role should survive, inherited Admin should be discarded");
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyRemoval_RestoresPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user6";
        const string ns = "org/removable";

        await securityService.AddUserRoleAsync(userId, "Admin", ns, "system", TestTimeout);
        await securityService.SetPolicyAsync(ns, new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false }, TestTimeout);

        var cappedPerms = await securityService.GetEffectivePermissionsAsync(ns, userId, TestTimeout);
        cappedPerms.Should().Be(Permission.Read | Permission.Execute, "permissions should be capped");

        await securityService.RemovePolicyAsync(ns, TestTimeout);

        var restoredPerms = await securityService.GetEffectivePermissionsAsync(ns, userId, TestTimeout);
        restoredPerms.Should().Be(Permission.All, "permissions should be restored after policy removal");
    }

    [Fact(Timeout = 20000)]
    public async Task SetGetPolicy_RoundTrip()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string ns = "org/roundtrip";

        var policy = new PartitionAccessPolicy
        {
            Create = false,
            Update = false,
            Delete = false,
            BreaksInheritance = true
        };

        await securityService.SetPolicyAsync(ns, policy, TestTimeout);

        var retrieved = await securityService.GetPolicyAsync(ns, TestTimeout);
        retrieved.Should().NotBeNull();
        retrieved!.Create.Should().Be(false);
        retrieved.Update.Should().Be(false);
        retrieved.Delete.Should().Be(false);
        retrieved.Read.Should().BeNull("null means inherit / allowed");
        retrieved.Comment.Should().BeNull("null means inherit / allowed");
        retrieved.BreaksInheritance.Should().BeTrue();
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyAppliesToPublicUser()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string ns = "org/public_capped";

        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", ns, "system", TestTimeout);
        await securityService.SetPolicyAsync(ns, new PartitionAccessPolicy { Read = false, Create = false, Update = false, Delete = false, Comment = false }, TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(ns, WellKnownUsers.Public, TestTimeout);
        permissions.Should().Be(Permission.Execute, "Public user permissions should be capped to Execute only (Read denied by policy)");
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyAtGlobalScope_CapsEverything()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user7";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);
        await securityService.SetPolicyAsync("", new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false }, TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("any/random/path", userId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute, "global policy should cap all namespaces to Read + Execute");

        // Cleanup global policy
        await securityService.RemovePolicyAsync("", TestTimeout);
    }
}

/// <summary>
/// Tests that static node providers (Doc, Agent, Role) enforce read-only policies
/// via PartitionAccessPolicy nodes emitted from their GetStaticNodes() methods.
/// </summary>
public class StaticNamespacePolicyTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddDocumentation()
            .AddRowLevelSecurity();
    }

    [Fact(Timeout = 20000)]
    public async Task DocNamespace_AdminCappedToReadOnly()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "admin_doc";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("Doc/GettingStarted", userId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute, "Doc namespace has a static read-only policy");
    }

    [Fact(Timeout = 20000)]
    public async Task DocNamespace_EditorCappedToReadOnly()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "editor_doc";

        await securityService.AddUserRoleAsync(userId, "Editor", "Doc", "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("Doc/AI/AgenticAI", userId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute, "Doc namespace has a static read-only policy");
    }

    [Fact(Timeout = 20000)]
    public async Task AgentNamespace_AdminCappedToReadOnly()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "admin_agent";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("Agent/ThreadNamer", userId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute, "Agent namespace has a static read-only policy");
    }

    [Fact(Timeout = 20000)]
    public async Task RoleNamespace_AdminCappedToReadOnly()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "admin_role";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("Role/Admin", userId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute, "Role namespace has a static read-only policy");
    }

    [Fact(Timeout = 20000)]
    public async Task StaticPolicy_DoesNotAffectOtherNamespaces()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "admin_other";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("ACME/Project/Task1", userId, TestTimeout);
        permissions.Should().Be(Permission.All, "ACME is not a static namespace, Admin should have full access");
    }

    [Fact(Timeout = 20000)]
    public async Task StaticPolicy_DocRootItselfCapped()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "admin_docroot";

        await securityService.AddUserRoleAsync(userId, "Admin", "", "system", TestTimeout);

        // The policy is at "Doc" namespace — nodes AT "Doc" should also be capped
        var permissions = await securityService.GetEffectivePermissionsAsync("Doc", userId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute, "Doc root itself should be capped to Read + Execute");
    }
}
