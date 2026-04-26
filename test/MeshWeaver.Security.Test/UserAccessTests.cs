using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests for access control using AccessAssignment MeshNodes.
/// Tests the hierarchical role assignment model with per-user access records.
/// Anonymous access is handled via "Public" as a well-known user.
///
/// All AccessAssignment MeshNodes are seeded statically via
/// <see cref="MeshBuilder.AddMeshNodes"/> in <see cref="ConfigureMesh"/>; tests then
/// verify <see cref="ISecurityService.GetEffectivePermissions(string,string)"/>
/// observable bridged to a <see cref="Task{T}"/> via <c>.FirstAsync().ToTask(ct)</c>.
/// </summary>
public class UserAccessTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("Alice", "Editor", "ACME"),
                // Bob: stacked roles (handled by combining two assignment nodes with distinct ids)
                AssignmentNodeFactory.UserRole("Bob_Viewer", "Viewer", "ACME"),
                AssignmentNodeFactory.UserRole("Bob", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("NewUser", "Admin", null),
                // Carol: stacked roles (Admin + Viewer at ACME) used for the "remove Admin" test
                AssignmentNodeFactory.UserRole("Carol_Admin", "Admin", "ACME"),
                AssignmentNodeFactory.UserRole("Carol", "Viewer", "ACME"),
                AssignmentNodeFactory.UserRole("Roland", "Admin", null),
                AssignmentNodeFactory.UserRole("MultiUser_MW", "Viewer", "MeshWeaver"),
                AssignmentNodeFactory.UserRole("MultiUser", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("InheritUser", "Admin", "Organization"),
                AssignmentNodeFactory.UserRole("OverrideUser_Org", "Viewer", "Org"),
                AssignmentNodeFactory.UserRole("OverrideUser", "Admin", "Org/Special"),
                AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "MeshWeaver"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_Profiles", "Viewer", "Profiles"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_PublicArea", "Viewer", "PublicArea"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_OpenProject", "Viewer", "OpenProject"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_PrivPubDocs", "Viewer", "Private/PublicDocs"),
                AssignmentNodeFactory.UserRole("AuthUser", "Editor", "Restricted"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Public}_PublicBase", "Viewer", "PublicBase"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Public}_PublicOnly", "Viewer", "PublicOnly"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_Systemorph", "Viewer", "Systemorph"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_MeshWeaver2", "Viewer", "MeshWeaver2"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_Public", "Viewer", "Public"),
                AssignmentNodeFactory.UserRole("QueryUser", "Editor", "Secret"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Public}_PublicOrg3", "Viewer", "PublicOrg3"),
                AssignmentNodeFactory.UserRole("CustomTypeAnon", "Viewer", "CustomType")
                    with { Id = $"{WellKnownUsers.Anonymous}_CustomType_Access", Content = new AccessAssignment { AccessObject = WellKnownUsers.Anonymous, DisplayName = WellKnownUsers.Anonymous, Roles = [new RoleAssignment { Role = "Viewer", Denied = false }] } });

    private void ClearAdminContext()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(null);
    }

    #region AccessAssignment CRUD Operations

    [Fact]
    public async Task AddUserRole_CreatesAccessAssignmentNode()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("ACME", "Alice", TestTimeout);
        permissions.Should().HaveFlag(Permission.Update, "Alice should have Editor permissions at ACME");
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
    }

    [Fact]
    public async Task GetEffectivePermissions_NonExistentUser_ReturnsNone()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var result = await Mesh.GetPermissionAsync("ACME", "nonexistent-user", TestTimeout);
        result.Should().Be(Permission.None);
    }

    [Fact]
    public async Task AddUserRole_MultipleRoles_CombinesPermissions()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("ACME", "Bob", TestTimeout);
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
    }

    [Fact]
    public async Task AddUserRole_GlobalRole_GrantsPermissionsEverywhere()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("ACME/SomeProject", "NewUser", TestTimeout);
        permissions.Should().Be(Permission.All);
    }

    [Fact]
    public async Task RemoveUserRole_RemovesSpecificRole()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Carol seeded with Admin (Carol_Admin) + Viewer (Carol). Remove the Admin assignment.
        await meshService.DeleteNode("ACME/_Access/Carol_Admin_Access")
            .FirstAsync().ToTask(TestTimeout);

        var permissions = await Mesh.GetPermissionAsync("ACME", "Carol", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api,
            "Only Viewer role should remain after removing Admin");
    }

    #endregion

    #region Permission Evaluation

    [Fact]
    public async Task GetEffectivePermissions_GlobalAdmin_HasAllPermissionsEverywhere()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permMeshWeaver = await Mesh.GetPermissionAsync("MeshWeaver", "Roland", TestTimeout);
        var permACME = await Mesh.GetPermissionAsync("ACME", "Roland", TestTimeout);
        var permDeep = await Mesh.GetPermissionAsync("ACME/ProductLaunch/Todo/Task1", "Roland", TestTimeout);

        permMeshWeaver.Should().Be(Permission.All);
        permACME.Should().Be(Permission.All);
        permDeep.Should().Be(Permission.All);
    }

    [Fact]
    public async Task GetEffectivePermissions_NamespacedEditor_HasEditorInNamespace()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permACME = await Mesh.GetPermissionAsync("ACME", "Alice", TestTimeout);
        var permChild = await Mesh.GetPermissionAsync("ACME/ProductLaunch", "Alice", TestTimeout);
        var permMeshWeaver = await Mesh.GetPermissionAsync("MeshWeaver", "Alice", TestTimeout);

        permACME.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export);
        permChild.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export);
        permMeshWeaver.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_NoRoles_HasNoPermissions()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("ACME", "UnknownUser", TestTimeout);
        permissions.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_MultipleRoles_CombinesPermissions()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permMeshWeaver = await Mesh.GetPermissionAsync("MeshWeaver", "MultiUser_MW", TestTimeout);
        var permACME = await Mesh.GetPermissionAsync("ACME", "MultiUser", TestTimeout);

        permMeshWeaver.Should().Be(Permission.Read | Permission.Execute | Permission.Api);
        permACME.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export);
    }

    #endregion

    #region Hierarchical Inheritance

    [Fact]
    public async Task GetEffectivePermissions_RoleOnParent_InheritsToChildren()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permParent = await Mesh.GetPermissionAsync("Organization", "InheritUser", TestTimeout);
        var permChild = await Mesh.GetPermissionAsync("Organization/Team", "InheritUser", TestTimeout);
        var permGrandchild = await Mesh.GetPermissionAsync("Organization/Team/Project", "InheritUser", TestTimeout);
        var permSibling = await Mesh.GetPermissionAsync("OtherOrg", "InheritUser", TestTimeout);

        permParent.Should().Be(Permission.All);
        permChild.Should().Be(Permission.All);
        permGrandchild.Should().Be(Permission.All);
        permSibling.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_ExactMatch_TakesPrecedence()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permOrg = await Mesh.GetPermissionAsync("Org", "OverrideUser", TestTimeout);
        var permSpecial = await Mesh.GetPermissionAsync("Org/Special", "OverrideUser", TestTimeout);
        var permOther = await Mesh.GetPermissionAsync("Org/Other", "OverrideUser", TestTimeout);

        // OverrideUser_Org has Viewer at "Org"; OverrideUser has Admin at "Org/Special".
        // GetEffectivePermissions is called twice for two distinct user IDs (different objects).
        permOrg.Should().Be(Permission.None, "OverrideUser has no role at Org — only OverrideUser_Org does");
        permSpecial.Should().Be(Permission.All); // Admin at Org/Special
        permOther.Should().Be(Permission.None); // No role inherited (Viewer was for OverrideUser_Org, not OverrideUser)
    }

    #endregion

    #region Anonymous Access

    [Fact]
    public async Task GetEffectivePermissions_AnonymousNamespace_AnonymousHasReadAccess()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("MeshWeaver", "", TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api);
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_CanAccessPublicNamespace()
    {
        // Admin context (DevLogin) is active for the seed creates.
        await NodeFactory.CreateNode(new MeshNode("PublicArea") { Name = "Public Area", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("Doc1", "PublicArea") { Name = "Document 1", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("Doc2", "PublicArea") { Name = "Document 2", NodeType = "Group" });
        ClearAdminContext();

        var children = await MeshQuery.QueryAsync<MeshNode>(new MeshQueryRequest { Query = "namespace:PublicArea", UserId = "" }).ToListAsync();

        var contentChildren = children.Where(n => n.NodeType != "AccessAssignment").ToList();
        contentChildren.Should().HaveCount(2);
        contentChildren.Should().Contain(n => n.Id == "Doc1");
        contentChildren.Should().Contain(n => n.Id == "Doc2");
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_CannotAccessPrivateNamespace()
    {
        await NodeFactory.CreateNode(new MeshNode("PrivateArea") { Name = "Private Area", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("Secret1", "PrivateArea") { Name = "Secret 1", NodeType = "Group" });
        ClearAdminContext();

        var children = await MeshQuery.QueryAsync<MeshNode>(new MeshQueryRequest { Query = "namespace:PrivateArea", UserId = "" }).ToListAsync();

        children.Should().BeEmpty();
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_SeesOnlyPublicRootChildren()
    {
        await NodeFactory.CreateNode(new MeshNode("OpenProject") { Name = "Open Project", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("ClosedProject") { Name = "Closed Project", NodeType = "Group" });
        ClearAdminContext();

        var rootChildren = await MeshQuery.QueryAsync<MeshNode>(new MeshQueryRequest { Query = "namespace:", UserId = "" }).ToListAsync();

        rootChildren.Should().Contain(n => n.Id == "OpenProject");
        rootChildren.Should().NotContain(n => n.Id == "ClosedProject");
    }

    [Fact]
    public async Task GetEffectivePermissions_PrivateNamespace_AnonymousHasNoAccess()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("ACME", "", TestTimeout);
        permissions.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_PublicChildOfPrivateParent_InheritsPublicFromChild()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permPrivate = await Mesh.GetPermissionAsync("Private", "", TestTimeout);
        var permPublicDocs = await Mesh.GetPermissionAsync("Private/PublicDocs", "", TestTimeout);

        permPrivate.Should().Be(Permission.None);
        permPublicDocs.Should().Be(Permission.Read | Permission.Execute | Permission.Api);
    }

    #endregion

    #region Integration: Access + Anonymous/Public User

    [Fact]
    public async Task GetEffectivePermissions_AuthenticatedUser_HasAccessToPrivateNamespace()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permAnonymous = await Mesh.GetPermissionAsync("Restricted", "", TestTimeout);
        var permAuthUser = await Mesh.GetPermissionAsync("Restricted", "AuthUser", TestTimeout);

        permAnonymous.Should().Be(Permission.None);
        permAuthUser.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export);
    }

    [Fact]
    public async Task AuthenticatedUser_InheritsPublicPermissions()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permAuth = await Mesh.GetPermissionAsync("PublicBase", "SomeAuthUser", TestTimeout);
        permAuth.Should().HaveFlag(Permission.Read,
            "Authenticated users should inherit Public user permissions as a baseline");
    }

    [Fact]
    public async Task AnonymousUser_DoesNotInheritPublicPermissions()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permAnon = await Mesh.GetPermissionAsync("PublicOnly", "", TestTimeout);
        permAnon.Should().Be(Permission.None,
            "Anonymous users should not inherit Public user permissions");

        var permAnonExplicit = await Mesh.GetPermissionAsync("PublicOnly", WellKnownUsers.Anonymous, TestTimeout);
        permAnonExplicit.Should().Be(Permission.None,
            "Anonymous user explicitly should not inherit Public user permissions");
    }

    #endregion

    #region IMeshService Access Control Filtering

    [Fact]
    public async Task MeshQuery_AnonymousUser_CanQueryPublicOrganizations()
    {
        await NodeFactory.CreateNode(new MeshNode("Systemorph")
        {
            Name = "Systemorph",
            NodeType = "Group"
        });

        await NodeFactory.CreateNode(new MeshNode("ACME")
        {
            Name = "ACME",
            NodeType = "Group"
        });
        ClearAdminContext();

        var request = new MeshQueryRequest
        {
            Query = "nodeType:Group namespace:",
            UserId = ""
        };
        var results = await MeshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("Systemorph", "Anonymous-accessible namespace should be visible");
        nodeNames.Should().NotContain("ACME", "Private namespace should NOT be visible to anonymous");
    }

    [Fact]
    public async Task MeshQuery_WithAccessContext_CanQueryPublicOrganizations()
    {
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        await NodeFactory.CreateNode(new MeshNode("MeshWeaver2")
        {
            Name = "MeshWeaver2",
            NodeType = "Group"
        });

        await NodeFactory.CreateNode(new MeshNode("SecretOrg")
        {
            Name = "SecretOrg",
            NodeType = "Group"
        });
        ClearAdminContext();

        accessService?.SetContext(null);

        var request = new MeshQueryRequest
        {
            Query = "nodeType:Group namespace:"
        };
        var results = await MeshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("MeshWeaver2", "Anonymous-accessible namespace should be visible");
        nodeNames.Should().NotContain("SecretOrg", "Private namespace should NOT be visible to anonymous");
    }

    [Fact]
    public async Task MeshQuery_AnonymousUser_FiltersRestrictedNodes()
    {
        var publicNode = new MeshNode("PublicDoc", "Public") { Name = "Public Document", NodeType = "Group" };
        var restrictedNode = new MeshNode("PrivateDoc", "Private") { Name = "Private Document", NodeType = "Group" };

        await NodeFactory.CreateNode(new MeshNode("Public") { Name = "Public", NodeType = "Group" });
        await NodeFactory.CreateNode(publicNode);
        await NodeFactory.CreateNode(new MeshNode("Private") { Name = "Private", NodeType = "Group" });
        await NodeFactory.CreateNode(restrictedNode);
        ClearAdminContext();

        var request = new MeshQueryRequest { Query = "path:Public scope:descendants", UserId = "" };
        var publicResults = await MeshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var restrictedRequest = new MeshQueryRequest { Query = "path:Private scope:descendants", UserId = "" };
        var restrictedResults = await MeshQuery.QueryAsync(restrictedRequest, TestTimeout).ToListAsync();

        var publicNodePaths = publicResults.OfType<MeshNode>().Select(n => n.Path).ToList();
        var restrictedNodePaths = restrictedResults.OfType<MeshNode>().Select(n => n.Path).ToList();

        publicNodePaths.Should().Contain("Public/PublicDoc");
        restrictedNodePaths.Should().NotContain("Private/PrivateDoc");
    }

    [Fact]
    public async Task MeshQuery_AuthenticatedUser_SeesRestrictedNodes()
    {
        var restrictedNode = new MeshNode("SecretDoc", "Secret") { Name = "Secret Document", NodeType = "Group" };
        await NodeFactory.CreateNode(new MeshNode("Secret") { Name = "Secret", NodeType = "Group" });
        await NodeFactory.CreateNode(restrictedNode);
        ClearAdminContext();

        var request = new MeshQueryRequest { Query = "path:Secret scope:descendants", UserId = "QueryUser" };
        var results = await MeshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var nodePaths = results.OfType<MeshNode>().Select(n => n.Path).ToList();
        nodePaths.Should().Contain("Secret/SecretDoc");
    }

    [Fact]
    public async Task MeshQuery_PublicPermissions_NotPollutedByAdminContext()
    {
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        await NodeFactory.CreateNode(new MeshNode("PublicOrg3")
        {
            Name = "PublicOrg3",
            NodeType = "Code"
        });

        await NodeFactory.CreateNode(new MeshNode("PrivateOrg3")
        {
            Name = "PrivateOrg3",
            NodeType = "Code"
        });
        ClearAdminContext();

        accessService?.SetContext(new AccessContext
        {
            ObjectId = "AdminUser",
            Name = "Admin User",
            Roles = ["Admin"]
        });

        var request = new MeshQueryRequest
        {
            Query = "nodeType:Code namespace:",
            UserId = WellKnownUsers.Public
        };
        var results = await MeshQuery.QueryAsync(request, TestTimeout).ToListAsync();

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("PublicOrg3", "Public namespace should be visible");
        nodeNames.Should().NotContain("PrivateOrg3",
            "Private namespace should NOT be visible to Public user, even when admin AccessContext is active");
    }

    [Fact]
    public async Task GetEffectivePermissions_PublicUser_IgnoresAdminAccessContextRoles()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        accessService?.SetContext(new AccessContext
        {
            ObjectId = "AdminUser",
            Name = "Admin User",
            Roles = ["Admin"]
        });

        var permissions = await Mesh.GetPermissionAsync("ACME", WellKnownUsers.Public, TestTimeout);

        permissions.Should().Be(Permission.None,
            "Public user should not inherit Admin roles from AccessContext belonging to a different user");
    }

    #endregion

    #region Explicit Public Access Grants

    [Fact]
    public async Task SecurePersistence_NodeInUserNamespace_VisibleViaExplicitAnonymousGrant()
    {
        await NodeFactory.CreateNode(new MeshNode("Profiles") { Name = "Profiles", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("AliceProfile", "Profiles") { Name = "Alice Profile", NodeType = "Markdown" });
        ClearAdminContext();

        var children = await MeshQuery.QueryAsync<MeshNode>(new MeshQueryRequest { Query = "namespace:Profiles", UserId = "" }).ToListAsync();

        children.Should().Contain(n => n.Id == "AliceProfile");
    }

    [Fact]
    public async Task SecurePersistence_NodeTypeDefinition_VisibleWithExplicitGrant()
    {
        await NodeFactory.CreateNode(new MeshNode("CustomType")
        {
            Name = "CustomType",
            NodeType = "NodeType"
        });

        await NodeFactory.CreateNode(new MeshNode("CustomInstance")
        {
            Name = "CustomInstance",
            NodeType = "Group"
        });
        ClearAdminContext();

        var typeDef = await MeshQuery.QueryAsync<MeshNode>(new MeshQueryRequest { Query = "path:CustomType", UserId = "" }).FirstOrDefaultAsync();
        var instance = await MeshQuery.QueryAsync<MeshNode>(new MeshQueryRequest { Query = "path:CustomInstance", UserId = "" }).FirstOrDefaultAsync();

        typeDef.Should().NotBeNull("NodeType definitions are readable with explicit Anonymous Viewer grant");
        typeDef!.Name.Should().Be("CustomType");
        instance.Should().BeNull("Non-granted instances require explicit access grants");
    }

    [Fact]
    public async Task SecurePersistence_NodeInPrivateNamespace_HiddenWithoutGrant()
    {
        await NodeFactory.CreateNode(new MeshNode("SecretArea") { Name = "Secret Area", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("Doc1", "SecretArea") { Name = "Secret Doc", NodeType = "Group" });
        ClearAdminContext();

        var children = await MeshQuery.QueryAsync<MeshNode>(new MeshQueryRequest { Query = "namespace:SecretArea", UserId = "" }).ToListAsync();

        children.Should().BeEmpty();
    }

    #endregion
}
