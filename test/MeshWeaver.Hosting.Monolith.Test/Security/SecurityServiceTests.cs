using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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

namespace MeshWeaver.Hosting.Monolith.Test.Security;

/// <summary>
/// Unit tests for the SecurityService implementation.
/// </summary>
public class SecurityServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder).AddRowLevelSecurity();
    }

    [Fact(Timeout = 10000)]
    public async Task GetEffectivePermissions_WithAdminRole_ReturnsAllPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user123";
        const string nodePath = "org/acme/project";

        await securityService.AddUserRoleAsync(userId, "Admin", nodePath, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 10000)]
    public async Task GetEffectivePermissions_WithViewerRole_ReturnsReadOnly()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "viewer123";
        const string nodePath = "org/acme/docs";

        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        permissions.Should().Be(Permission.Read);
    }

    [Fact(Timeout = 10000)]
    public async Task GetEffectivePermissions_WithEditorRole_ReturnsReadCreateUpdate()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "editor123";
        const string nodePath = "org/acme/project/docs";

        await securityService.AddUserRoleAsync(userId, "Editor", nodePath, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment);
    }

    [Fact(Timeout = 10000)]
    public async Task GetEffectivePermissions_NoRoles_ReturnsNone()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "newuser";
        const string nodePath = "org/private/secure";

        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        permissions.Should().Be(Permission.None);
    }

    [Fact(Timeout = 10000)]
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

    [Fact(Timeout = 10000)]
    public async Task GetEffectivePermissions_WithGlobalRole_AppliesEverywhere()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "globaladmin";
        const string anyPath = "some/random/path";

        await securityService.AddUserRoleAsync(userId, "Admin", null!, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(anyPath, userId, TestTimeout);

        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 10000)]
    public async Task GetEffectivePermissions_CombinesMultipleRoles()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "multiuser";
        const string path1 = "org/project1";
        const string path2 = "org/project1/subproject";

        await securityService.AddUserRoleAsync(userId, "Viewer", path1, "system", TestTimeout);
        await securityService.AddUserRoleAsync(userId, "Editor", path2, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(path2, userId, TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment);
    }

    [Fact(Timeout = 10000)]
    public async Task HasPermission_WithSufficientPermissions_ReturnsTrue()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "readuser";
        const string nodePath = "org/docs/readme";

        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        var canRead = await securityService.HasPermissionAsync(nodePath, userId, Permission.Read, TestTimeout);
        canRead.Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public async Task HasPermission_WithoutSufficientPermissions_ReturnsFalse()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "readonlyuser";
        const string nodePath = "org/restricted/data";

        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        var canDelete = await securityService.HasPermissionAsync(nodePath, userId, Permission.Delete, TestTimeout);
        canDelete.Should().BeFalse();
    }

    [Fact(Timeout = 10000)]
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

    [Fact(Timeout = 10000)]
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

    [Fact(Timeout = 10000)]
    public async Task PublicUser_GetsAnonymousPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string targetNamespace = "org/public/area";

        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", targetNamespace, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync(targetNamespace, "", TestTimeout);

        permissions.Should().Be(Permission.Read);
    }

    [Fact(Timeout = 10000)]
    public async Task GetRole_ReturnsBuiltInRole()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var adminRole = await securityService.GetRoleAsync("Admin", TestTimeout);

        adminRole.Should().NotBeNull();
        adminRole!.Permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 10000)]
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

    [Fact(Timeout = 10000)]
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
        return base.ConfigureMesh(builder).AddRowLevelSecurity();
    }

    [Fact(Timeout = 10000)]
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

    [Fact(Timeout = 10000)]
    public async Task ValidateAsync_WithPermission_ReturnsValid()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        const string userId = "authorized-user";

        await securityService.AddUserRoleAsync(userId, "Admin", "allowed/area", "system", TestTimeout);

        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = new MeshNode("test", "allowed/area") { Name = "Test Node" },
            AccessContext = new AccessContext { ObjectId = userId }
        };

        var result = await validator!.ValidateAsync(context, TestTimeout);

        result.IsValid.Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public void SupportedOperations_ReturnsCreateUpdateDeleteOperations()
    {
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        var operations = validator!.SupportedOperations;

        operations.Should().NotContain(NodeOperation.Read);
        operations.Should().Contain(NodeOperation.Create);
        operations.Should().Contain(NodeOperation.Update);
        operations.Should().Contain(NodeOperation.Delete);
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

    private static string SamplesDataPath
    {
        get
        {
            var currentDir = AppContext.BaseDirectory;
            var solutionDir = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", ".."));
            return Path.Combine(solutionDir, "samples", "Graph", "Data");
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(SamplesDataPath)
            .AddRowLevelSecurity();
    }

    [Fact(Timeout = 10000)]
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

    [Fact(Timeout = 10000)]
    public async Task Roland_GlobalAdmin_CanEditAnyNode()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Roland";

        var paths = new[]
        {
            "MeshWeaver/Documentation/Architecture",
            "Systemorph",
            "Demos/ACME",
            "some/random/path"
        };

        foreach (var path in paths)
        {
            var canEdit = await securityService.HasPermissionAsync(path, userId, Permission.Update, TestTimeout);
            canEdit.Should().BeTrue($"Roland should be able to edit '{path}' as global Admin");
        }
    }

    [Fact(Timeout = 10000)]
    public async Task Alice_WithAcmeEditorRole_CanEditInAcmeOnly()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Alice";

        var canEditAcme = await securityService.HasPermissionAsync("Demos/ACME/Project/Task1", userId, Permission.Update, TestTimeout);
        var canEditMeshWeaver = await securityService.HasPermissionAsync("MeshWeaver/Documentation", userId, Permission.Update, TestTimeout);

        canEditAcme.Should().BeTrue("Alice should be able to edit in Demos/ACME namespace");
        canEditMeshWeaver.Should().BeFalse("Alice should NOT be able to edit in MeshWeaver namespace");
    }

    [Fact(Timeout = 10000)]
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
}
