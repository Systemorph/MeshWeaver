using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
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
        // Add Row-Level Security services
        builder.AddRowLevelSecurity();

        // Configure role configuration for "project" NodeType
        builder.WithRoleConfiguration("project", config => config
            .WithAdminRole()
            .WithEditorRole()
            .WithViewerRole()
            .WithInheritance());

        // Configure public access for "public-doc" NodeType
        builder.WithRoleConfiguration("public-doc", config => config
            .AsPublic(Permission.Read)
            .WithViewerRole());

        return base.ConfigureMesh(builder);
    }

    [Fact]
    public async Task GetEffectivePermissions_WithAdminRole_ReturnsAllPermissions()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "user123";
        const string nodePath = "org/acme/project";

        // Assign Admin role to user
        await securityService.AssignRoleAsync(userId, "Admin", nodePath, "system", TestTimeout);

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
        await securityService.AssignRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

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
        await securityService.AssignRoleAsync(userId, "Editor", nodePath, "system", TestTimeout);

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
        await securityService.AssignRoleAsync(userId, "Admin", parentPath, "system", TestTimeout);

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
        await securityService.AssignRoleAsync(userId, "Admin", null!, "system", TestTimeout);

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
        await securityService.AssignRoleAsync(userId, "Viewer", path1, "system", TestTimeout);
        await securityService.AssignRoleAsync(userId, "Editor", path2, "system", TestTimeout);

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
        await securityService.AssignRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

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
        await securityService.AssignRoleAsync(userId, "Viewer", nodePath, "system", TestTimeout);

        // Act & Assert - Should not have Delete permission
        var canDelete = await securityService.HasPermissionAsync(nodePath, userId, Permission.Delete, TestTimeout);
        canDelete.Should().BeFalse();
    }

    [Fact]
    public async Task AssignRole_CreatesAssignment()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "newassignee";
        const string nodePath = "org/newproject";

        // Act
        await securityService.AssignRoleAsync(userId, "Editor", nodePath, "admin", TestTimeout);

        // Assert
        var assignments = await securityService.GetUserRoleAssignmentsAsync(userId, TestTimeout).ToListAsync();
        assignments.Should().ContainSingle(a =>
            a.UserId == userId &&
            a.RoleId == "Editor" &&
            a.NodePath == nodePath);
    }

    [Fact]
    public async Task RemoveRoleAssignment_RemovesAssignment()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "removetest";
        const string nodePath = "org/removeproject";

        await securityService.AssignRoleAsync(userId, "Admin", nodePath, "admin", TestTimeout);

        // Act
        await securityService.RemoveRoleAssignmentAsync(userId, "Admin", nodePath, TestTimeout);

        // Assert
        var assignments = await securityService.GetUserRoleAssignmentsAsync(userId, TestTimeout).ToListAsync();
        assignments.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserRoleAssignments_ReturnsAllAssignments()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string userId = "multiassign";

        await securityService.AssignRoleAsync(userId, "Admin", "org/project1", "admin", TestTimeout);
        await securityService.AssignRoleAsync(userId, "Viewer", "org/project2", "admin", TestTimeout);
        await securityService.AssignRoleAsync(userId, "Editor", "org/project3", "admin", TestTimeout);

        // Act
        var assignments = await securityService.GetUserRoleAssignmentsAsync(userId, TestTimeout).ToListAsync();

        // Assert
        assignments.Should().HaveCount(3);
        assignments.Select(a => a.RoleId).Should().Contain(["Admin", "Viewer", "Editor"]);
    }

    [Fact]
    public async Task GetNodeRoleAssignments_ReturnsAssignmentsForNode()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        const string nodePath = "org/shared/project";

        await securityService.AssignRoleAsync("user1", "Admin", nodePath, "system", TestTimeout);
        await securityService.AssignRoleAsync("user2", "Editor", nodePath, "system", TestTimeout);
        await securityService.AssignRoleAsync("user3", "Viewer", nodePath, "system", TestTimeout);

        // Act
        var assignments = await securityService.GetNodeRoleAssignmentsAsync(nodePath, TestTimeout).ToListAsync();

        // Assert
        assignments.Should().HaveCount(3);
        assignments.Select(a => a.UserId).Should().Contain(["user1", "user2", "user3"]);
    }

    [Fact]
    public async Task SetRoleConfiguration_PersistsConfiguration()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var customRole = new Role
        {
            Id = "CustomRole",
            DisplayName = "Custom Role",
            Permissions = Permission.Read | Permission.Create,
            IsInheritable = true
        };

        var config = new RoleConfiguration
        {
            NodeType = "custom-type",
            Roles = new Dictionary<string, Role> { { "CustomRole", customRole } },
            IsPublic = false,
            InheritFromParent = true
        };

        // Act
        await securityService.SetRoleConfigurationAsync(config, TestTimeout);

        // Assert
        var retrievedConfig = await securityService.GetRoleConfigurationAsync("custom-type", TestTimeout);
        retrievedConfig.Should().NotBeNull();
        retrievedConfig!.NodeType.Should().Be("custom-type");
        retrievedConfig.Roles.Should().ContainKey("CustomRole");
    }

    [Fact]
    public async Task SetNodeSecurityConfiguration_PersistsOverrides()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var nodeConfig = new NodeSecurityConfiguration
        {
            NodePath = "org/special/node",
            IsPublicOverride = true,
            AnonymousPermissionsOverride = Permission.Read,
            InheritFromParentOverride = false
        };

        // Act
        await securityService.SetNodeSecurityConfigurationAsync(nodeConfig, TestTimeout);

        // Assert
        var retrievedConfig = await securityService.GetNodeSecurityConfigurationAsync("org/special/node", TestTimeout);
        retrievedConfig.Should().NotBeNull();
        retrievedConfig!.IsPublicOverride.Should().BeTrue();
        retrievedConfig.AnonymousPermissionsOverride.Should().Be(Permission.Read);
        retrievedConfig.InheritFromParentOverride.Should().BeFalse();
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
        // Add Row-Level Security services (includes RlsNodeValidator)
        builder.AddRowLevelSecurity();

        // Assign global admin role for test setup
        builder.WithGlobalRoleAssignment("testadmin", "Admin");

        return base.ConfigureMesh(builder);
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
        await securityService.AssignRoleAsync(userId, "Admin", "allowed/area", "system", TestTimeout);

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
