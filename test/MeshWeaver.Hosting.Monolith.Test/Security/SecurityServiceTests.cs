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
        // First configure base (adds persistence), then add Row-Level Security
        // RLS must be added after persistence so it can decorate IPersistenceService
        return base.ConfigureMesh(builder).AddRowLevelSecurity();
    }

    [Fact]
    public async Task GetEffectivePermissions_WithAdminRole_ReturnsAllPermissions()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user123";
        const string nodePath = "org/acme/project";

        // Assign Admin role to user
        await securityService.AddUserRoleAsync(userId, "Admin", nodePath, "system", TestTimeout);

        // Act
        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        // Assert
        permissions.Should().Be(Permission.All);
    }

    [Fact]
    public async Task GetEffectivePermissions_WithViewerRole_ReturnsReadOnly()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "viewer123";
        const string nodePath = "org/acme/docs";

        // Assign Viewer role to user
        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        // Act
        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        // Assert
        permissions.Should().Be(Permission.Read);
    }

    [Fact]
    public async Task GetEffectivePermissions_WithEditorRole_ReturnsReadCreateUpdate()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "editor123";
        const string nodePath = "org/acme/project/docs";

        // Assign Editor role to user
        await securityService.AddUserRoleAsync(userId, "Editor", nodePath, "system", TestTimeout);

        // Act
        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        // Assert
        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update);
    }

    [Fact]
    public async Task GetEffectivePermissions_NoRoles_ReturnsNone()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "newuser";
        const string nodePath = "org/private/secure";

        // Act - No role assigned
        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);

        // Assert
        permissions.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_WithInheritance_InheritsFromParent()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "inherituser";
        const string parentPath = "org/parent";
        const string childPath = "org/parent/child/grandchild";

        // Assign Admin role at parent level
        await securityService.AddUserRoleAsync(userId, "Admin", parentPath, "system", TestTimeout);

        // Act - Check permissions at child level
        var permissions = await securityService.GetEffectivePermissionsAsync(childPath, userId, TestTimeout);

        // Assert - Should inherit Admin permissions from parent
        permissions.Should().Be(Permission.All);
    }

    [Fact]
    public async Task GetEffectivePermissions_WithGlobalRole_AppliesEverywhere()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "globaladmin";
        const string anyPath = "some/random/path";

        // Assign global Admin role (null nodePath)
        await securityService.AddUserRoleAsync(userId, "Admin", null!, "system", TestTimeout);

        // Act - Check permissions at any path
        var permissions = await securityService.GetEffectivePermissionsAsync(anyPath, userId, TestTimeout);

        // Assert - Should have Admin permissions globally
        permissions.Should().Be(Permission.All);
    }

    [Fact]
    public async Task GetEffectivePermissions_CombinesMultipleRoles()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "multiuser";
        const string path1 = "org/project1";
        const string path2 = "org/project1/subproject";

        // Assign Viewer at parent, Editor at child
        await securityService.AddUserRoleAsync(userId, "Viewer", path1, "system", TestTimeout);
        await securityService.AddUserRoleAsync(userId, "Editor", path2, "system", TestTimeout);

        // Act - Check permissions at child path
        var permissions = await securityService.GetEffectivePermissionsAsync(path2, userId, TestTimeout);

        // Assert - Should combine Viewer (Read) + Editor (Read, Create, Update) = Read | Create | Update
        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update);
    }

    [Fact]
    public async Task HasPermission_WithSufficientPermissions_ReturnsTrue()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "readuser";
        const string nodePath = "org/docs/readme";

        // Assign Viewer role
        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        // Act & Assert
        var canRead = await securityService.HasPermissionAsync(nodePath, userId, Permission.Read, TestTimeout);
        canRead.Should().BeTrue();
    }

    [Fact]
    public async Task HasPermission_WithoutSufficientPermissions_ReturnsFalse()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "readonlyuser";
        const string nodePath = "org/restricted/data";

        // Assign Viewer role (Read only)
        await securityService.AddUserRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        // Act & Assert - Should not have Delete permission
        var canDelete = await securityService.HasPermissionAsync(nodePath, userId, Permission.Delete, TestTimeout);
        canDelete.Should().BeFalse();
    }

    [Fact]
    public async Task AddUserRole_CreatesAssignment()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "newassignee";
        const string targetNamespace = "org/newproject";

        // Act
        await securityService.AddUserRoleAsync(userId, "Editor", targetNamespace, "admin", TestTimeout);

        // Assert - With per-namespace storage, we need to specify the namespace to find the user
        var userAccess = await securityService.GetUserAccessAsync(userId, targetNamespace, TestTimeout);
        userAccess.Should().NotBeNull();
        userAccess!.Roles.Should().ContainSingle(r => r.RoleId == "Editor");
    }

    [Fact]
    public async Task RemoveUserRole_RemovesAssignment()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "removetest";
        const string targetNamespace = "org/removeproject";

        await securityService.AddUserRoleAsync(userId, "Admin", targetNamespace, "admin", TestTimeout);

        // Act
        await securityService.RemoveUserRoleAsync(userId, "Admin", targetNamespace, TestTimeout);

        // Assert - With per-namespace storage, user should have no access in this namespace after removal
        var userAccess = await securityService.GetUserAccessAsync(userId, targetNamespace, TestTimeout);
        var hasRole = userAccess?.Roles.Any(r => r.RoleId == "Admin") ?? false;
        hasRole.Should().BeFalse();
    }

    [Fact]
    public async Task PublicUser_GetsAnonymousPermissions()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string targetNamespace = "org/public/area";

        // Grant Viewer role to "Public" user (represents anonymous access)
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", targetNamespace, "system", TestTimeout);

        // Act - Check anonymous permissions (empty userId)
        var permissions = await securityService.GetEffectivePermissionsAsync(targetNamespace, "", TestTimeout);

        // Assert - Public user should have Viewer (Read) permissions
        permissions.Should().Be(Permission.Read);
    }

    [Fact]
    public async Task GetRole_ReturnsBuiltInRole()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Act
        var adminRole = await securityService.GetRoleAsync("Admin", TestTimeout);

        // Assert
        adminRole.Should().NotBeNull();
        adminRole!.Permissions.Should().Be(Permission.All);
    }

    [Fact]
    public async Task SaveRole_PersistsCustomRole()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var customRole = new Role
        {
            Id = "Contributor",
            DisplayName = "Contributor",
            Permissions = Permission.Read | Permission.Create,
            IsInheritable = true
        };

        // Act
        await securityService.SaveRoleAsync(customRole, TestTimeout);

        // Assert
        var retrievedRole = await securityService.GetRoleAsync("Contributor", TestTimeout);
        retrievedRole.Should().NotBeNull();
        retrievedRole!.Permissions.Should().Be(Permission.Read | Permission.Create);
    }

    [Fact]
    public async Task GetRoles_ReturnsAllRoles()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Save a custom role
        await securityService.SaveRoleAsync(new Role
        {
            Id = "Auditor",
            Permissions = Permission.Read
        }, TestTimeout);

        // Act
        var roles = await securityService.GetRolesAsync(TestTimeout).ToListAsync();

        // Assert - Should include built-in roles and custom role
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
        // First configure base (adds persistence), then add Row-Level Security
        // RLS must be added after persistence so it can decorate IPersistenceService
        return base.ConfigureMesh(builder).AddRowLevelSecurity();
    }

    [Fact]
    public async Task ValidateAsync_WithoutPermission_ReturnsUnauthorized()
    {
        // Arrange
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

        // Act
        var result = await validator!.ValidateAsync(context, TestTimeout);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.Unauthorized);
    }

    [Fact]
    public async Task ValidateAsync_WithPermission_ReturnsValid()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        const string userId = "authorized-user";

        // Grant Admin permissions
        await securityService.AddUserRoleAsync(userId, "Admin", "allowed/area", "system", TestTimeout);

        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = new MeshNode("test", "allowed/area") { Name = "Test Node" },
            AccessContext = new AccessContext { ObjectId = userId }
        };

        // Act
        var result = await validator!.ValidateAsync(context, TestTimeout);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void SupportedOperations_ReturnsCreateUpdateDeleteOperations()
    {
        // Arrange
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        // Act
        var operations = validator!.SupportedOperations;

        // Assert - Read is handled separately via SecurePersistenceServiceDecorator
        operations.Should().NotContain(NodeOperation.Read);
        operations.Should().Contain(NodeOperation.Create);
        operations.Should().Contain(NodeOperation.Update);
        operations.Should().Contain(NodeOperation.Delete);
    }
}

/// <summary>
/// Tests for security using the sample Graph data (samples/Graph/Data).
/// Tests real-world scenarios with existing access configuration files.
/// </summary>
public class SampleDataSecurityTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    // Get the samples/Graph/Data path relative to the test project
    private static string SamplesDataPath
    {
        get
        {
            // Navigate from test output directory to samples/Graph/Data
            var currentDir = AppContext.BaseDirectory;
            var solutionDir = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", ".."));
            return Path.Combine(solutionDir, "samples", "Graph", "Data");
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Use file system persistence with the samples data folder
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(SamplesDataPath)
            .AddRowLevelSecurity();
    }

    [Fact]
    public async Task Roland_WithGlobalAdminRole_CanEditArchitectureNode()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Roland"; // From samples/Graph/Data/Access/Roland.json
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        // Act - Check if Roland has Update permission (editability)
        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);
        var canEdit = await securityService.HasPermissionAsync(nodePath, userId, Permission.Update, TestTimeout);

        // Assert
        permissions.Should().Be(Permission.All, "Roland has global Admin role with null namespace");
        canEdit.Should().BeTrue("Roland should be able to edit the Architecture node");
    }

    [Fact]
    public async Task Roland_GlobalAdmin_CanEditAnyNode()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Roland";

        // Test various paths across the mesh
        var paths = new[]
        {
            "MeshWeaver/Documentation/Architecture",
            "Systemorph",
            "ACME",
            "some/random/path"
        };

        foreach (var path in paths)
        {
            // Act
            var canEdit = await securityService.HasPermissionAsync(path, userId, Permission.Update, TestTimeout);

            // Assert
            canEdit.Should().BeTrue($"Roland should be able to edit '{path}' as global Admin");
        }
    }

    [Fact]
    public async Task Alice_WithAcmeEditorRole_CanEditInAcmeOnly()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Alice"; // From samples/Graph/Data/ACME/Access/Alice.json

        // Act - Check ACME namespace
        var acmePermissions = await securityService.GetEffectivePermissionsAsync("ACME/Project/Task1", userId, TestTimeout);
        var canEditAcme = await securityService.HasPermissionAsync("ACME/Project/Task1", userId, Permission.Update, TestTimeout);

        // Act - Check MeshWeaver namespace (should NOT have access)
        var meshWeaverPermissions = await securityService.GetEffectivePermissionsAsync("MeshWeaver/Documentation", userId, TestTimeout);
        var canEditMeshWeaver = await securityService.HasPermissionAsync("MeshWeaver/Documentation", userId, Permission.Update, TestTimeout);

        // Assert
        canEditAcme.Should().BeTrue("Alice should be able to edit in ACME namespace");
        canEditMeshWeaver.Should().BeFalse("Alice should NOT be able to edit in MeshWeaver namespace");
    }

    [Fact]
    public async Task PublicUser_WithMeshWeaverViewerRole_CannotEdit()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Public"; // From samples/Graph/Data/MeshWeaver/Access/Public.json
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        // Act
        var permissions = await securityService.GetEffectivePermissionsAsync(nodePath, userId, TestTimeout);
        var canEdit = await securityService.HasPermissionAsync(nodePath, userId, Permission.Update, TestTimeout);
        var canRead = await securityService.HasPermissionAsync(nodePath, userId, Permission.Read, TestTimeout);

        // Assert
        canRead.Should().BeTrue("Public user should be able to read MeshWeaver content");
        canEdit.Should().BeFalse("Public user should NOT be able to edit MeshWeaver content");
    }

    [Fact]
    public async Task GetUserAccess_Roland_ReturnsGlobalAdminRole()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "Roland";

        // Act
        var userAccess = await securityService.GetUserAccessAsync(userId, TestTimeout);

        // Assert
        userAccess.Should().NotBeNull();
        userAccess!.UserId.Should().Be("Roland");
        userAccess.DisplayName.Should().Be("Roland Buergi");
        userAccess.Roles.Should().ContainSingle(r => r.RoleId == "Admin");
    }
}
