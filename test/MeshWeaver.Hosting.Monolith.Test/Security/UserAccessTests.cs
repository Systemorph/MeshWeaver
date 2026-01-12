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
/// Tests for UserAccess-based access control (Access partition).
/// Tests the hierarchical role assignment model with per-user access records.
/// Anonymous access is handled via "Public" as a well-known user.
/// </summary>
public class UserAccessTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // First configure base (adds persistence), then add Row-Level Security
        // RLS must be added after persistence so it can decorate IPersistenceService
        return base.ConfigureMesh(builder).AddRowLevelSecurity();
    }

    #region UserAccess CRUD Operations

    [Fact]
    public async Task SaveUserAccess_CreatesNewUserAccess()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var userAccess = new UserAccess
        {
            UserId = "Alice",
            DisplayName = "Alice Chen",
            Roles = [new UserRole { RoleId = "Editor" }]
        };

        // Act - Save to ACME namespace
        await securityService.AddUserRoleAsync("Alice", "Editor", "ACME", "system", TestTimeout);

        // Assert - With per-namespace storage, we need to specify the namespace to find the user
        var retrieved = await securityService.GetUserAccessAsync("Alice", "ACME", TestTimeout);
        retrieved.Should().NotBeNull();
        retrieved!.UserId.Should().Be("Alice");
        retrieved.Roles.Should().HaveCount(1);
        retrieved.Roles[0].RoleId.Should().Be("Editor");
    }

    [Fact]
    public async Task GetUserAccess_NonExistentUser_ReturnsNull()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Act
        var result = await securityService.GetUserAccessAsync("nonexistent-user", TestTimeout);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task AddUserRole_AddsRoleToExistingUser()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Bob", "Viewer", "ACME", "system", TestTimeout);

        // Act - Add another role to same namespace
        await securityService.AddUserRoleAsync("Bob", "Editor", "ACME", "system", TestTimeout);

        // Assert - With per-namespace storage, we need to specify the namespace to find the user
        var retrieved = await securityService.GetUserAccessAsync("Bob", "ACME", TestTimeout);
        retrieved.Should().NotBeNull();
        retrieved!.Roles.Should().HaveCount(2);
        retrieved.Roles.Should().Contain(r => r.RoleId == "Viewer");
        retrieved.Roles.Should().Contain(r => r.RoleId == "Editor");
    }

    [Fact]
    public async Task AddUserRole_ToNewUser_CreatesUserAccess()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Act - Add global role (null namespace)
        await securityService.AddUserRoleAsync("NewUser", "Admin", null, "system", TestTimeout);

        // Assert
        var retrieved = await securityService.GetUserAccessAsync("NewUser", TestTimeout);
        retrieved.Should().NotBeNull();
        retrieved!.UserId.Should().Be("NewUser");
        retrieved.Roles.Should().ContainSingle(r => r.RoleId == "Admin");
    }

    [Fact]
    public async Task RemoveUserRole_RemovesSpecificRole()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Carol", "Admin", "ACME", "system", TestTimeout);
        await securityService.AddUserRoleAsync("Carol", "Viewer", "ACME", "system", TestTimeout);

        // Act
        await securityService.RemoveUserRoleAsync("Carol", "Admin", "ACME", TestTimeout);

        // Assert - With per-namespace storage, we need to specify the namespace to find the user
        var retrieved = await securityService.GetUserAccessAsync("Carol", "ACME", TestTimeout);
        retrieved.Should().NotBeNull();
        retrieved!.Roles.Should().ContainSingle(r => r.RoleId == "Viewer");
    }

    #endregion

    #region Permission Evaluation with UserAccess

    [Fact]
    public async Task GetEffectivePermissions_GlobalAdmin_HasAllPermissionsEverywhere()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Roland", "Admin", null, "system", TestTimeout); // Global Admin

        // Act & Assert - Should have full permissions anywhere
        var permMeshWeaver = await securityService.GetEffectivePermissionsAsync("MeshWeaver", "Roland", TestTimeout);
        var permACME = await securityService.GetEffectivePermissionsAsync("ACME", "Roland", TestTimeout);
        var permDeep = await securityService.GetEffectivePermissionsAsync("ACME/ProductLaunch/Todo/Task1", "Roland", TestTimeout);

        permMeshWeaver.Should().Be(Permission.All);
        permACME.Should().Be(Permission.All);
        permDeep.Should().Be(Permission.All);
    }

    [Fact]
    public async Task GetEffectivePermissions_NamespacedEditor_HasEditorInNamespace()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Alice", "Editor", "ACME", "system", TestTimeout);

        // Act
        var permACME = await securityService.GetEffectivePermissionsAsync("ACME", "Alice", TestTimeout);
        var permChild = await securityService.GetEffectivePermissionsAsync("ACME/ProductLaunch", "Alice", TestTimeout);
        var permMeshWeaver = await securityService.GetEffectivePermissionsAsync("MeshWeaver", "Alice", TestTimeout);

        // Assert
        permACME.Should().Be(Permission.Read | Permission.Create | Permission.Update);
        permChild.Should().Be(Permission.Read | Permission.Create | Permission.Update); // Inherited
        permMeshWeaver.Should().Be(Permission.None); // No access to other namespaces
    }

    [Fact]
    public async Task GetEffectivePermissions_NoRoles_HasNoPermissions()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Act - User with no roles
        var permissions = await securityService.GetEffectivePermissionsAsync("ACME", "UnknownUser", TestTimeout);

        // Assert
        permissions.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_MultipleRoles_CombinesPermissions()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("MultiUser", "Viewer", "MeshWeaver", "system", TestTimeout);
        await securityService.AddUserRoleAsync("MultiUser", "Editor", "ACME", "system", TestTimeout);

        // Act
        var permMeshWeaver = await securityService.GetEffectivePermissionsAsync("MeshWeaver", "MultiUser", TestTimeout);
        var permACME = await securityService.GetEffectivePermissionsAsync("ACME", "MultiUser", TestTimeout);

        // Assert
        permMeshWeaver.Should().Be(Permission.Read); // Viewer
        permACME.Should().Be(Permission.Read | Permission.Create | Permission.Update); // Editor
    }

    #endregion

    #region Hierarchical Inheritance

    [Fact]
    public async Task GetEffectivePermissions_RoleOnParent_InheritsToChildren()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("InheritUser", "Admin", "Organization", "system", TestTimeout);

        // Act
        var permParent = await securityService.GetEffectivePermissionsAsync("Organization", "InheritUser", TestTimeout);
        var permChild = await securityService.GetEffectivePermissionsAsync("Organization/Team", "InheritUser", TestTimeout);
        var permGrandchild = await securityService.GetEffectivePermissionsAsync("Organization/Team/Project", "InheritUser", TestTimeout);
        var permSibling = await securityService.GetEffectivePermissionsAsync("OtherOrg", "InheritUser", TestTimeout);

        // Assert
        permParent.Should().Be(Permission.All);
        permChild.Should().Be(Permission.All); // Inherited from parent
        permGrandchild.Should().Be(Permission.All); // Inherited from grandparent
        permSibling.Should().Be(Permission.None); // No access to sibling namespace
    }

    [Fact]
    public async Task GetEffectivePermissions_ExactMatch_TakesPrecedence()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("OverrideUser", "Viewer", "Org", "system", TestTimeout);
        await securityService.AddUserRoleAsync("OverrideUser", "Admin", "Org/Special", "system", TestTimeout);

        // Act
        var permOrg = await securityService.GetEffectivePermissionsAsync("Org", "OverrideUser", TestTimeout);
        var permSpecial = await securityService.GetEffectivePermissionsAsync("Org/Special", "OverrideUser", TestTimeout);
        var permOther = await securityService.GetEffectivePermissionsAsync("Org/Other", "OverrideUser", TestTimeout);

        // Assert
        permOrg.Should().Be(Permission.Read); // Viewer at Org level
        permSpecial.Should().Be(Permission.All); // Admin at Org/Special (combines Viewer from parent + Admin)
        permOther.Should().Be(Permission.Read); // Only Viewer inherited from Org
    }

    #endregion

    #region GetUsersWithAccessToNamespace

    [Fact]
    public async Task GetUsersWithAccessToNamespace_ReturnsMatchingUsers()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        await securityService.AddUserRoleAsync("User1", "Admin", null, "system", TestTimeout); // Global
        await securityService.AddUserRoleAsync("User2", "Editor", "ACME", "system", TestTimeout); // ACME
        await securityService.AddUserRoleAsync("User3", "Viewer", "MeshWeaver", "system", TestTimeout); // Other namespace

        // Act
        var usersWithACMEAccess = await securityService.GetUsersWithAccessToNamespaceAsync("ACME", TestTimeout).ToListAsync();
        var usersWithChildAccess = await securityService.GetUsersWithAccessToNamespaceAsync("ACME/ProductLaunch", TestTimeout).ToListAsync();

        // Assert
        usersWithACMEAccess.Should().HaveCount(2);
        usersWithACMEAccess.Should().Contain(u => u.UserId == "User1"); // Global admin
        usersWithACMEAccess.Should().Contain(u => u.UserId == "User2"); // ACME editor

        usersWithChildAccess.Should().HaveCount(2);
        usersWithChildAccess.Should().Contain(u => u.UserId == "User1"); // Global admin
        usersWithChildAccess.Should().Contain(u => u.UserId == "User2"); // ACME editor (inherited)
    }

    #endregion

    #region Anonymous Access with Public User

    [Fact]
    public async Task GetEffectivePermissions_PublicNamespace_AnonymousHasReadAccess()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Configure MeshWeaver as public by adding Viewer role to "Public" user
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "MeshWeaver", "system", TestTimeout);

        // Act - Anonymous user (empty userId)
        var permissions = await securityService.GetEffectivePermissionsAsync("MeshWeaver", "", TestTimeout);

        // Assert
        permissions.Should().Be(Permission.Read);
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_CanAccessPublicNamespace()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Create test namespace and nodes
        await persistenceService.SaveNodeAsync(new MeshNode("PublicArea") { Name = "Public Area" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Doc1", "PublicArea") { Name = "Document 1" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Doc2", "PublicArea") { Name = "Document 2" }, TestTimeout);

        // Configure Public user to have Viewer role on PublicArea
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "PublicArea", "system", TestTimeout);

        // Act - Get children as anonymous user (null/empty userId)
        var children = await persistenceService.GetChildrenSecureAsync("PublicArea", null).ToListAsync();

        // Assert - Anonymous user should see the public documents
        children.Should().HaveCount(2);
        children.Should().Contain(n => n.Id == "Doc1");
        children.Should().Contain(n => n.Id == "Doc2");
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_CannotAccessPrivateNamespace()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Create private namespace and nodes (no Public access)
        await persistenceService.SaveNodeAsync(new MeshNode("PrivateArea") { Name = "Private Area" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Secret1", "PrivateArea") { Name = "Secret 1" }, TestTimeout);

        // Don't configure any Public access

        // Act - Get children as anonymous user (null/empty userId)
        var children = await persistenceService.GetChildrenSecureAsync("PrivateArea", null).ToListAsync();

        // Assert - Anonymous user should not see any documents
        children.Should().BeEmpty();
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_SeesOnlyPublicRootChildren()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Create two root namespaces
        await persistenceService.SaveNodeAsync(new MeshNode("OpenProject") { Name = "Open Project" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("ClosedProject") { Name = "Closed Project" }, TestTimeout);

        // Only configure Public access for OpenProject
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "OpenProject", "system", TestTimeout);

        // Act - Get root children as anonymous user
        var rootChildren = await persistenceService.GetChildrenSecureAsync(null, null).ToListAsync();

        // Assert - Only OpenProject should be visible
        rootChildren.Should().Contain(n => n.Id == "OpenProject");
        rootChildren.Should().NotContain(n => n.Id == "ClosedProject");
    }

    [Fact]
    public async Task GetEffectivePermissions_PrivateNamespace_AnonymousHasNoAccess()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // ACME has no Public user access configured

        // Act - Anonymous user (empty userId)
        var permissions = await securityService.GetEffectivePermissionsAsync("ACME", "", TestTimeout);

        // Assert
        permissions.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_PublicChildOfPrivateParent_InheritsPublicFromChild()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Configure parent as private (no Public access), child as public
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "Private/PublicDocs", "system", TestTimeout);

        // Act
        var permPrivate = await securityService.GetEffectivePermissionsAsync("Private", "", TestTimeout);
        var permPublicDocs = await securityService.GetEffectivePermissionsAsync("Private/PublicDocs", "", TestTimeout);

        // Assert
        permPrivate.Should().Be(Permission.None);
        permPublicDocs.Should().Be(Permission.Read);
    }

    #endregion

    #region Integration: UserAccess + Public User

    [Fact]
    public async Task GetEffectivePermissions_AuthenticatedUser_HasAccessToPrivateNamespace()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Restricted namespace has no public access
        // Give user explicit access
        await securityService.AddUserRoleAsync("AuthUser", "Editor", "Restricted", "system", TestTimeout);

        // Act
        var permAnonymous = await securityService.GetEffectivePermissionsAsync("Restricted", "", TestTimeout);
        var permAuthUser = await securityService.GetEffectivePermissionsAsync("Restricted", "AuthUser", TestTimeout);

        // Assert
        permAnonymous.Should().Be(Permission.None); // Anonymous blocked
        permAuthUser.Should().Be(Permission.Read | Permission.Create | Permission.Update); // Has Editor role
    }

    #endregion

    #region IMeshQuery Access Control Filtering

    [Fact]
    public async Task MeshQuery_AnonymousUser_CanQueryPublicOrganizations()
    {
        // Arrange - Set up structure similar to samples/Graph/Data
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        // Create Organization NodeType
        await persistenceService.SaveNodeAsync(new MeshNode("Organization")
        {
            Name = "Organization",
            NodeType = "NodeType",
            Description = "An organization containing projects"
        }, TestTimeout);

        // Create two organizations - one public (Systemorph), one private (ACME)
        await persistenceService.SaveNodeAsync(new MeshNode("Systemorph")
        {
            Name = "Systemorph",
            NodeType = "Organization",
            Description = "The company behind MeshWeaver"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("ACME")
        {
            Name = "ACME",
            NodeType = "Organization",
            Description = "Private organization"
        }, TestTimeout);

        // Configure Systemorph as public (Public user has Viewer role)
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "Systemorph", "system", TestTimeout);

        // ACME remains private (no Public access)

        // Act - Query for all organizations as anonymous user (empty userId)
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Organization scope:children",
            UserId = "" // Anonymous
        };
        var results = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        // Assert - Anonymous should only see Systemorph, not ACME
        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("Systemorph", "Public namespace should be visible");
        nodeNames.Should().NotContain("ACME", "Private namespace should NOT be visible to anonymous");
    }

    [Fact]
    public async Task MeshQuery_WithAccessContext_CanQueryPublicOrganizations()
    {
        // Arrange - This simulates the real portal scenario where AccessService context is set
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        // Create Organization NodeType
        await persistenceService.SaveNodeAsync(new MeshNode("Organization2")
        {
            Name = "Organization2",
            NodeType = "NodeType",
            Description = "An organization containing projects"
        }, TestTimeout);

        // Create two organizations - one public (MeshWeaver), one private (SecretOrg)
        await persistenceService.SaveNodeAsync(new MeshNode("MeshWeaver2")
        {
            Name = "MeshWeaver2",
            NodeType = "Organization2",
            Description = "The MeshWeaver project"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("SecretOrg")
        {
            Name = "SecretOrg",
            NodeType = "Organization2",
            Description = "Secret organization"
        }, TestTimeout);

        // Configure MeshWeaver2 as public (Public user has Viewer role)
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "MeshWeaver2", "system", TestTimeout);

        // SecretOrg remains private (no Public access)

        // Simulate anonymous user: AccessService context is null (no authenticated user)
        accessService?.SetContext(null);

        // Act - Query for all organizations WITHOUT specifying UserId
        // This should use AccessService to get the current user (null -> empty -> "Public")
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Organization2 scope:children"
            // Note: UserId is NOT set - should come from AccessService
        };
        var results = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        // Assert - Anonymous should only see MeshWeaver2, not SecretOrg
        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("MeshWeaver2", "Public namespace should be visible");
        nodeNames.Should().NotContain("SecretOrg", "Private namespace should NOT be visible to anonymous");
    }

    [Fact]
    public async Task MeshQuery_AnonymousUser_FiltersRestrictedNodes()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        // Create test nodes (Id, Namespace) - Path is derived as Namespace/Id or just Id
        var publicNode = new MeshNode("PublicDoc", "Public") { Name = "Public Document" };
        var restrictedNode = new MeshNode("PrivateDoc", "Private") { Name = "Private Document" };

        await persistenceService.SaveNodeAsync(new MeshNode("Public") { Name = "Public" }, TestTimeout);
        await persistenceService.SaveNodeAsync(publicNode, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Private") { Name = "Private" }, TestTimeout);
        await persistenceService.SaveNodeAsync(restrictedNode, TestTimeout);

        // Configure Public namespace as public (Public user has Viewer role)
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "Public", "system", TestTimeout);

        // Private namespace has no Public user access

        // Act - Query as anonymous user (empty userId)
        var request = new MeshQueryRequest { Query = "path:Public scope:descendants", UserId = "" };
        var publicResults = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var restrictedRequest = new MeshQueryRequest { Query = "path:Private scope:descendants", UserId = "" };
        var restrictedResults = await meshQuery.QueryAsync(restrictedRequest, TestTimeout).ToListAsync();

        // Assert - Use OfType for cleaner filtering
        var publicNodePaths = publicResults.OfType<MeshNode>().Select(n => n.Path).ToList();
        var restrictedNodePaths = restrictedResults.OfType<MeshNode>().Select(n => n.Path).ToList();

        publicNodePaths.Should().Contain("Public/PublicDoc");
        restrictedNodePaths.Should().NotContain("Private/PrivateDoc");
    }

    [Fact]
    public async Task MeshQuery_AuthenticatedUser_SeesRestrictedNodes()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        // Create test node in restricted area (Id, Namespace)
        var restrictedNode = new MeshNode("SecretDoc", "Secret") { Name = "Secret Document" };
        await persistenceService.SaveNodeAsync(new MeshNode("Secret") { Name = "Secret" }, TestTimeout);
        await persistenceService.SaveNodeAsync(restrictedNode, TestTimeout);

        // Secret namespace has no public access
        // Give user access
        await securityService.AddUserRoleAsync("QueryUser", "Editor", "Secret", "system", TestTimeout);

        // Act - Query as authenticated user
        var request = new MeshQueryRequest { Query = "path:Secret scope:descendants", UserId = "QueryUser" };
        var results = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        // Assert - User should see the restricted node
        var nodePaths = results.OfType<MeshNode>().Select(n => n.Path).ToList();
        nodePaths.Should().Contain("Secret/SecretDoc");
    }

    #endregion
}
