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
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test.Security;

/// <summary>
/// Tests for access control using AccessAssignment MeshNodes.
/// Tests the hierarchical role assignment model with per-user access records.
/// Anonymous access is handled via "Public" as a well-known user.
/// </summary>
public class UserAccessTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder).AddRowLevelSecurity();
    }

    #region AccessAssignment CRUD Operations

    [Fact]
    public async Task AddUserRole_CreatesAccessAssignmentNode()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        await securityService.AddUserRoleAsync("Alice", "Editor", "ACME", "system", TestTimeout);

        // Verify via permission check
        var permissions = await securityService.GetEffectivePermissionsAsync("ACME", "Alice", TestTimeout);
        permissions.Should().HaveFlag(Permission.Update, "Alice should have Editor permissions at ACME");
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
    }

    [Fact]
    public async Task GetEffectivePermissions_NonExistentUser_ReturnsNone()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var result = await securityService.GetEffectivePermissionsAsync("ACME", "nonexistent-user", TestTimeout);

        result.Should().Be(Permission.None);
    }

    [Fact]
    public async Task AddUserRole_MultipleRoles_CombinesPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Bob", "Viewer", "ACME", "system", TestTimeout);
        await securityService.AddUserRoleAsync("Bob", "Editor", "ACME", "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("ACME", "Bob", TestTimeout);
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
    }

    [Fact]
    public async Task AddUserRole_GlobalRole_GrantsPermissionsEverywhere()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        await securityService.AddUserRoleAsync("NewUser", "Admin", null, "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("ACME/SomeProject", "NewUser", TestTimeout);
        permissions.Should().Be(Permission.All);
    }

    [Fact]
    public async Task RemoveUserRole_RemovesSpecificRole()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Carol", "Admin", "ACME", "system", TestTimeout);
        await securityService.AddUserRoleAsync("Carol", "Viewer", "ACME", "system", TestTimeout);

        await securityService.RemoveUserRoleAsync("Carol", "Admin", "ACME", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("ACME", "Carol", TestTimeout);
        permissions.Should().Be(Permission.Read, "Only Viewer role should remain after removing Admin");
    }

    #endregion

    #region Permission Evaluation

    [Fact]
    public async Task GetEffectivePermissions_GlobalAdmin_HasAllPermissionsEverywhere()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Roland", "Admin", null, "system", TestTimeout);

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
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("Alice", "Editor", "ACME", "system", TestTimeout);

        var permACME = await securityService.GetEffectivePermissionsAsync("ACME", "Alice", TestTimeout);
        var permChild = await securityService.GetEffectivePermissionsAsync("ACME/ProductLaunch", "Alice", TestTimeout);
        var permMeshWeaver = await securityService.GetEffectivePermissionsAsync("MeshWeaver", "Alice", TestTimeout);

        permACME.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment);
        permChild.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment);
        permMeshWeaver.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_NoRoles_HasNoPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync("ACME", "UnknownUser", TestTimeout);

        permissions.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_MultipleRoles_CombinesPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("MultiUser", "Viewer", "MeshWeaver", "system", TestTimeout);
        await securityService.AddUserRoleAsync("MultiUser", "Editor", "ACME", "system", TestTimeout);

        var permMeshWeaver = await securityService.GetEffectivePermissionsAsync("MeshWeaver", "MultiUser", TestTimeout);
        var permACME = await securityService.GetEffectivePermissionsAsync("ACME", "MultiUser", TestTimeout);

        permMeshWeaver.Should().Be(Permission.Read);
        permACME.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment);
    }

    #endregion

    #region Hierarchical Inheritance

    [Fact]
    public async Task GetEffectivePermissions_RoleOnParent_InheritsToChildren()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("InheritUser", "Admin", "Organization", "system", TestTimeout);

        var permParent = await securityService.GetEffectivePermissionsAsync("Organization", "InheritUser", TestTimeout);
        var permChild = await securityService.GetEffectivePermissionsAsync("Organization/Team", "InheritUser", TestTimeout);
        var permGrandchild = await securityService.GetEffectivePermissionsAsync("Organization/Team/Project", "InheritUser", TestTimeout);
        var permSibling = await securityService.GetEffectivePermissionsAsync("OtherOrg", "InheritUser", TestTimeout);

        permParent.Should().Be(Permission.All);
        permChild.Should().Be(Permission.All);
        permGrandchild.Should().Be(Permission.All);
        permSibling.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_ExactMatch_TakesPrecedence()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync("OverrideUser", "Viewer", "Org", "system", TestTimeout);
        await securityService.AddUserRoleAsync("OverrideUser", "Admin", "Org/Special", "system", TestTimeout);

        var permOrg = await securityService.GetEffectivePermissionsAsync("Org", "OverrideUser", TestTimeout);
        var permSpecial = await securityService.GetEffectivePermissionsAsync("Org/Special", "OverrideUser", TestTimeout);
        var permOther = await securityService.GetEffectivePermissionsAsync("Org/Other", "OverrideUser", TestTimeout);

        permOrg.Should().Be(Permission.Read);
        permSpecial.Should().Be(Permission.All); // Admin at Org/Special + Viewer from parent
        permOther.Should().Be(Permission.Read); // Only Viewer inherited from Org
    }

    #endregion

    #region Anonymous Access

    [Fact]
    public async Task GetEffectivePermissions_AnonymousNamespace_AnonymousHasReadAccess()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", "MeshWeaver", "system", TestTimeout);

        var permissions = await securityService.GetEffectivePermissionsAsync("MeshWeaver", "", TestTimeout);

        permissions.Should().Be(Permission.Read);
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_CanAccessPublicNamespace()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        await persistenceService.SaveNodeAsync(new MeshNode("PublicArea") { Name = "Public Area" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Doc1", "PublicArea") { Name = "Document 1" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Doc2", "PublicArea") { Name = "Document 2" }, TestTimeout);

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", "PublicArea", "system", TestTimeout);

        var children = await persistenceService.GetChildrenSecureAsync("PublicArea", null).ToListAsync();

        // Filter out AccessAssignment nodes (infrastructure nodes for RLS)
        var contentChildren = children.Where(n => n.NodeType != "AccessAssignment").ToList();
        contentChildren.Should().HaveCount(2);
        contentChildren.Should().Contain(n => n.Id == "Doc1");
        contentChildren.Should().Contain(n => n.Id == "Doc2");
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_CannotAccessPrivateNamespace()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        await persistenceService.SaveNodeAsync(new MeshNode("PrivateArea") { Name = "Private Area" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Secret1", "PrivateArea") { Name = "Secret 1" }, TestTimeout);

        var children = await persistenceService.GetChildrenSecureAsync("PrivateArea", null).ToListAsync();

        children.Should().BeEmpty();
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_SeesOnlyPublicRootChildren()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        await persistenceService.SaveNodeAsync(new MeshNode("OpenProject") { Name = "Open Project" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("ClosedProject") { Name = "Closed Project" }, TestTimeout);

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", "OpenProject", "system", TestTimeout);

        var rootChildren = await persistenceService.GetChildrenSecureAsync(null, null).ToListAsync();

        rootChildren.Should().Contain(n => n.Id == "OpenProject");
        rootChildren.Should().NotContain(n => n.Id == "ClosedProject");
    }

    [Fact]
    public async Task GetEffectivePermissions_PrivateNamespace_AnonymousHasNoAccess()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync("ACME", "", TestTimeout);

        permissions.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_PublicChildOfPrivateParent_InheritsPublicFromChild()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", "Private/PublicDocs", "system", TestTimeout);

        var permPrivate = await securityService.GetEffectivePermissionsAsync("Private", "", TestTimeout);
        var permPublicDocs = await securityService.GetEffectivePermissionsAsync("Private/PublicDocs", "", TestTimeout);

        permPrivate.Should().Be(Permission.None);
        permPublicDocs.Should().Be(Permission.Read);
    }

    #endregion

    #region Integration: Access + Anonymous/Public User

    [Fact]
    public async Task GetEffectivePermissions_AuthenticatedUser_HasAccessToPrivateNamespace()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        await securityService.AddUserRoleAsync("AuthUser", "Editor", "Restricted", "system", TestTimeout);

        var permAnonymous = await securityService.GetEffectivePermissionsAsync("Restricted", "", TestTimeout);
        var permAuthUser = await securityService.GetEffectivePermissionsAsync("Restricted", "AuthUser", TestTimeout);

        permAnonymous.Should().Be(Permission.None);
        permAuthUser.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment);
    }

    [Fact]
    public async Task AuthenticatedUser_InheritsPublicPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Grant Public (logged-in baseline) Viewer on a namespace
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "PublicBase", "system", TestTimeout);

        // An authenticated user should inherit Public's Viewer permissions
        var permAuth = await securityService.GetEffectivePermissionsAsync("PublicBase", "SomeAuthUser", TestTimeout);
        permAuth.Should().HaveFlag(Permission.Read,
            "Authenticated users should inherit Public user permissions as a baseline");
    }

    [Fact]
    public async Task AnonymousUser_DoesNotInheritPublicPermissions()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Grant Public (logged-in baseline) Viewer on a namespace, but NOT Anonymous
        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "PublicOnly", "system", TestTimeout);

        // Anonymous should NOT inherit Public's permissions
        var permAnon = await securityService.GetEffectivePermissionsAsync("PublicOnly", "", TestTimeout);
        permAnon.Should().Be(Permission.None,
            "Anonymous users should not inherit Public user permissions");

        var permAnonExplicit = await securityService.GetEffectivePermissionsAsync("PublicOnly", WellKnownUsers.Anonymous, TestTimeout);
        permAnonExplicit.Should().Be(Permission.None,
            "Anonymous user explicitly should not inherit Public user permissions");
    }

    #endregion

    #region IMeshQuery Access Control Filtering

    [Fact]
    public async Task MeshQuery_AnonymousUser_CanQueryPublicOrganizations()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        await persistenceService.SaveNodeAsync(new MeshNode("Organization")
        {
            Name = "Organization",
            NodeType = "NodeType"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("Systemorph")
        {
            Name = "Systemorph",
            NodeType = "Organization"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("ACME")
        {
            Name = "ACME",
            NodeType = "Organization"
        }, TestTimeout);

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", "Systemorph", "system", TestTimeout);

        var request = new MeshQueryRequest
        {
            Query = "nodeType:Organization scope:children",
            UserId = ""
        };
        var results = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("Systemorph", "Anonymous-accessible namespace should be visible");
        nodeNames.Should().NotContain("ACME", "Private namespace should NOT be visible to anonymous");
    }

    [Fact]
    public async Task MeshQuery_WithAccessContext_CanQueryPublicOrganizations()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        await persistenceService.SaveNodeAsync(new MeshNode("Organization2")
        {
            Name = "Organization2",
            NodeType = "NodeType"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("MeshWeaver2")
        {
            Name = "MeshWeaver2",
            NodeType = "Organization2"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("SecretOrg")
        {
            Name = "SecretOrg",
            NodeType = "Organization2"
        }, TestTimeout);

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", "MeshWeaver2", "system", TestTimeout);

        accessService?.SetContext(null);

        var request = new MeshQueryRequest
        {
            Query = "nodeType:Organization2 scope:children"
        };
        var results = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("MeshWeaver2", "Anonymous-accessible namespace should be visible");
        nodeNames.Should().NotContain("SecretOrg", "Private namespace should NOT be visible to anonymous");
    }

    [Fact]
    public async Task MeshQuery_AnonymousUser_FiltersRestrictedNodes()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var publicNode = new MeshNode("PublicDoc", "Public") { Name = "Public Document" };
        var restrictedNode = new MeshNode("PrivateDoc", "Private") { Name = "Private Document" };

        await persistenceService.SaveNodeAsync(new MeshNode("Public") { Name = "Public" }, TestTimeout);
        await persistenceService.SaveNodeAsync(publicNode, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Private") { Name = "Private" }, TestTimeout);
        await persistenceService.SaveNodeAsync(restrictedNode, TestTimeout);

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", "Public", "system", TestTimeout);

        var request = new MeshQueryRequest { Query = "path:Public scope:descendants", UserId = "" };
        var publicResults = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var restrictedRequest = new MeshQueryRequest { Query = "path:Private scope:descendants", UserId = "" };
        var restrictedResults = await meshQuery.QueryAsync(restrictedRequest, TestTimeout).ToListAsync();

        var publicNodePaths = publicResults.OfType<MeshNode>().Select(n => n.Path).ToList();
        var restrictedNodePaths = restrictedResults.OfType<MeshNode>().Select(n => n.Path).ToList();

        publicNodePaths.Should().Contain("Public/PublicDoc");
        restrictedNodePaths.Should().NotContain("Private/PrivateDoc");
    }

    [Fact]
    public async Task MeshQuery_AuthenticatedUser_SeesRestrictedNodes()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var restrictedNode = new MeshNode("SecretDoc", "Secret") { Name = "Secret Document" };
        await persistenceService.SaveNodeAsync(new MeshNode("Secret") { Name = "Secret" }, TestTimeout);
        await persistenceService.SaveNodeAsync(restrictedNode, TestTimeout);

        await securityService.AddUserRoleAsync("QueryUser", "Editor", "Secret", "system", TestTimeout);

        var request = new MeshQueryRequest { Query = "path:Secret scope:descendants", UserId = "QueryUser" };
        var results = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var nodePaths = results.OfType<MeshNode>().Select(n => n.Path).ToList();
        nodePaths.Should().Contain("Secret/SecretDoc");
    }

    [Fact]
    public async Task MeshQuery_PublicPermissions_NotPollutedByAdminContext()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        await persistenceService.SaveNodeAsync(new MeshNode("OrgType3")
        {
            Name = "OrgType3",
            NodeType = "NodeType"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("PublicOrg3")
        {
            Name = "PublicOrg3",
            NodeType = "OrgType3"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("PrivateOrg3")
        {
            Name = "PrivateOrg3",
            NodeType = "OrgType3"
        }, TestTimeout);

        await securityService.AddUserRoleAsync(WellKnownUsers.Public, "Viewer", "PublicOrg3", "system", TestTimeout);

        accessService?.SetContext(new AccessContext
        {
            ObjectId = "AdminUser",
            Name = "Admin User",
            Roles = ["Admin"]
        });

        var request = new MeshQueryRequest
        {
            Query = "nodeType:OrgType3 scope:children",
            UserId = WellKnownUsers.Public
        };
        var results = await meshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("PublicOrg3", "Public namespace should be visible");
        nodeNames.Should().NotContain("PrivateOrg3",
            "Private namespace should NOT be visible to Public user, even when admin AccessContext is active");
    }

    [Fact]
    public async Task GetEffectivePermissions_PublicUser_IgnoresAdminAccessContextRoles()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        accessService?.SetContext(new AccessContext
        {
            ObjectId = "AdminUser",
            Name = "Admin User",
            Roles = ["Admin"]
        });

        var permissions = await securityService.GetEffectivePermissionsAsync("ACME", WellKnownUsers.Public, TestTimeout);

        permissions.Should().Be(Permission.None,
            "Public user should not inherit Admin roles from AccessContext belonging to a different user");
    }

    #endregion

    #region Explicit Public Access Grants

    [Fact]
    public async Task SecurePersistence_NodeInUserNamespace_VisibleViaExplicitAnonymousGrant()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        await persistenceService.SaveNodeAsync(new MeshNode("User") { Name = "User" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("AliceProfile", "User") { Name = "Alice Profile", NodeType = "Person" }, TestTimeout);

        await securityService.AddUserRoleAsync(WellKnownUsers.Anonymous, "Viewer", "User", "system", TestTimeout);

        var children = await persistenceService.GetChildrenSecureAsync("User", null).ToListAsync();

        children.Should().Contain(n => n.Id == "AliceProfile");
    }

    [Fact]
    public async Task SecurePersistence_NodeTypeDefinition_AlwaysVisibleToAnonymous()
    {
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        await persistenceService.SaveNodeAsync(new MeshNode("Organization")
        {
            Name = "Organization",
            NodeType = "NodeType"
        }, TestTimeout);

        await persistenceService.SaveNodeAsync(new MeshNode("ACME")
        {
            Name = "ACME",
            NodeType = "Organization"
        }, TestTimeout);

        var typeDef = await persistenceService.GetNodeSecureAsync("Organization", null);
        var orgInstance = await persistenceService.GetNodeSecureAsync("ACME", null);

        typeDef.Should().NotBeNull("NodeType definitions are always publicly readable");
        typeDef!.Name.Should().Be("Organization");
        orgInstance.Should().BeNull("Organization instances require explicit access grants");
    }

    [Fact]
    public async Task SecurePersistence_NodeInPrivateNamespace_HiddenWithoutGrant()
    {
        var persistenceService = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        await persistenceService.SaveNodeAsync(new MeshNode("SecretArea") { Name = "Secret Area" }, TestTimeout);
        await persistenceService.SaveNodeAsync(new MeshNode("Doc1", "SecretArea") { Name = "Secret Doc" }, TestTimeout);

        var children = await persistenceService.GetChildrenSecureAsync("SecretArea", null).ToListAsync();

        children.Should().BeEmpty();
    }

    #endregion
}
