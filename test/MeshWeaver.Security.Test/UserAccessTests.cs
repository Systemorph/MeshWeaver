using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
/// verify <see cref="SecurityService.GetEffectivePermissions(string,string)"/>
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
                AssignmentNodeFactory.UserRole("Bob_Viewer", "Viewer", "ACME", accessObject: "Bob"),
                AssignmentNodeFactory.UserRole("Bob", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("NewUser", "Admin", null),
                // Carol: only the static Viewer at ACME is seeded here. The
                // Admin role is created at runtime by RemoveUserRole_RemovesSpecificRole
                // so the same test can delete it again — static seeds live in
                // MeshConfiguration.Nodes only and DeleteNode would not find them.
                AssignmentNodeFactory.UserRole("Carol", "Viewer", "ACME"),
                AssignmentNodeFactory.UserRole("Roland", "Admin", null),
                // MultiUser_MW is an INDEPENDENT principal in MultipleRoles_CombinesPermissions —
                // accessObject defaults to the userId so the test's per-user-id queries find each.
                AssignmentNodeFactory.UserRole("MultiUser_MW", "Viewer", "MeshWeaver"),
                AssignmentNodeFactory.UserRole("MultiUser", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("InheritUser", "Admin", "Space"),
                // OverrideUser_Org is a separate principal in ExactMatch_TakesPrecedence —
                // OverrideUser must NOT inherit OverrideUser_Org's grant.
                AssignmentNodeFactory.UserRole("OverrideUser_Org", "Viewer", "Org"),
                AssignmentNodeFactory.UserRole("OverrideUser", "Admin", "Org/Special"),
                AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "MeshWeaver"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_Profiles", "Viewer", "Profiles", accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_PublicArea", "Viewer", "PublicArea", accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_OpenProject", "Viewer", "OpenProject", accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_PrivPubDocs", "Viewer", "Private/PublicDocs", accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole("AuthUser", "Editor", "Restricted"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Public}_PublicBase", "Viewer", "PublicBase", accessObject: WellKnownUsers.Public),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Public}_PublicOnly", "Viewer", "PublicOnly", accessObject: WellKnownUsers.Public),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_Systemorph", "Viewer", "Systemorph", accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_MeshWeaver2", "Viewer", "MeshWeaver2", accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Anonymous}_Public", "Viewer", "Public", accessObject: WellKnownUsers.Anonymous),
                AssignmentNodeFactory.UserRole("QueryUser", "Editor", "Secret"),
                AssignmentNodeFactory.UserRole($"{WellKnownUsers.Public}_PublicOrg3", "Viewer", "PublicOrg3", accessObject: WellKnownUsers.Public),
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
        var permissions = await Mesh.GetEffectivePermissions("ACME", "Alice")
            .Should().Match(p => p.HasFlag(Permission.Update) && p.HasFlag(Permission.Read) && p.HasFlag(Permission.Create));
        permissions.Should().HaveFlag(Permission.Update, "Alice should have Editor permissions at ACME");
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
    }

    [Fact]
    public async Task GetEffectivePermissions_NonExistentUser_ReturnsNone()
    {
        await Mesh.GetEffectivePermissions("ACME", "nonexistent-user")
            .Should().Match(p => p == Permission.None);
    }

    [Fact]
    public async Task AddUserRole_MultipleRoles_CombinesPermissions()
    {
        var permissions = await Mesh.GetEffectivePermissions("ACME", "Bob")
            .Should().Match(p => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Create) && p.HasFlag(Permission.Update));
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
    }

    [Fact]
    public async Task AddUserRole_GlobalRole_GrantsPermissionsEverywhere()
    {
        await Mesh.GetEffectivePermissions("ACME/SomeProject", "NewUser")
            .Should().Match(p => p == (Permission.All | Permission.Compile));
    }

    [Fact]
    public async Task RemoveUserRole_RemovesSpecificRole()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Re-create Carol's Admin assignment at runtime so the deletion below
        // hits a real persisted node. Static seeds (added via AddMeshNodes in
        // ConfigureMesh) live in MeshConfiguration.Nodes only — they're picked
        // up by SecurityService's static-seed path, but DeleteNode looks at
        // persistence storage and would return "Node not found".
        var carolAdmin = AssignmentNodeFactory.UserRole("Carol_RuntimeAdmin", "Admin", "ACME",
            accessObject: "Carol");
        await meshService.CreateNode(carolAdmin).Should().Emit();

        // ACME must always keep at least one administrator (SpaceAdminInvariantValidator),
        // so seed a co-admin before removing Carol's Admin — otherwise deleting Carol's
        // (sole) ACME admin is correctly rejected as "last administrator". Wait until the
        // read-side surface the validator reads (IMeshService.Query) shows the co-admin so
        // the delete below isn't racing eventual consistency.
        var acmeCoAdmin = AssignmentNodeFactory.UserRole("AcmeCoAdmin", "Admin", "ACME");
        await meshService.CreateNode(acmeCoAdmin).Should().Emit();
        await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{acmeCoAdmin.Path}"))
            .Where(c => c.Items.Any(n => n.Id == acmeCoAdmin.Id))
            .Take(1).Timeout(10.Seconds())
            .Should().Emit();

        // Wait for the runtime grant to surface so Admin is part of Carol's
        // permissions before we delete it again.
        await Mesh.GetEffectivePermissions("ACME", "Carol")
            .Should().Match(p => p.HasFlag(Permission.Delete));

        await meshService.DeleteNode(carolAdmin.Path).Should().Emit();

        // Wait for the deletion to surface (Delete must drop out of Carol's
        // permissions; the static Viewer seed remains).
        var permissions = await Mesh.GetEffectivePermissions("ACME", "Carol")
            .Should().Match(p => !p.HasFlag(Permission.Delete) && p.HasFlag(Permission.Read));
        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api,
            "Only Viewer role should remain after removing Admin");
    }

    #endregion

    #region Permission Evaluation

    [Fact]
    public async Task GetEffectivePermissions_GlobalAdmin_HasAllPermissionsEverywhere()
    {
        var permMeshWeaver = await Mesh.GetEffectivePermissions("MeshWeaver", "Roland").Should().Match(p => p == (Permission.All | Permission.Compile));
        var permACME = await Mesh.GetEffectivePermissions("ACME", "Roland").Should().Match(p => p == (Permission.All | Permission.Compile));
        var permDeep = await Mesh.GetEffectivePermissions("ACME/ProductLaunch/Todo/Task1", "Roland").Should().Match(p => p == (Permission.All | Permission.Compile));

        permMeshWeaver.Should().Be(Permission.All | Permission.Compile);
        permACME.Should().Be(Permission.All | Permission.Compile);
        permDeep.Should().Be(Permission.All | Permission.Compile);
    }

    [Fact]
    public async Task GetEffectivePermissions_NamespacedEditor_HasEditorInNamespace()
    {
        var editorPerms = Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export | Permission.Compile;
        var permACME = await Mesh.GetEffectivePermissions("ACME", "Alice").Should().Match(p => p == editorPerms);
        var permChild = await Mesh.GetEffectivePermissions("ACME/ProductLaunch", "Alice").Should().Match(p => p == editorPerms);
        var permMeshWeaver = await Mesh.GetEffectivePermissions("MeshWeaver", "Alice").Should().Match(p => p == Permission.None);

        permACME.Should().Be(editorPerms);
        permChild.Should().Be(editorPerms);
        permMeshWeaver.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_NoRoles_HasNoPermissions()
    {
        await Mesh.GetEffectivePermissions("ACME", "UnknownUser")
            .Should().Match(p => p == Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_MultipleRoles_CombinesPermissions()
    {
        var editorPerms = Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export | Permission.Compile;
        var permMeshWeaver = await Mesh.GetEffectivePermissions("MeshWeaver", "MultiUser_MW")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));
        var permACME = await Mesh.GetEffectivePermissions("ACME", "MultiUser").Should().Match(p => p == editorPerms);

        permMeshWeaver.Should().Be(Permission.Read | Permission.Execute | Permission.Api);
        permACME.Should().Be(editorPerms);
    }

    #endregion

    #region Hierarchical Inheritance

    [Fact]
    public async Task GetEffectivePermissions_RoleOnParent_InheritsToChildren()
    {
        var permParent = await Mesh.GetEffectivePermissions("Space", "InheritUser").Should().Match(p => p == (Permission.All | Permission.Compile));
        var permChild = await Mesh.GetEffectivePermissions("Space/Team", "InheritUser").Should().Match(p => p == (Permission.All | Permission.Compile));
        var permGrandchild = await Mesh.GetEffectivePermissions("Space/Team/Project", "InheritUser").Should().Match(p => p == (Permission.All | Permission.Compile));
        var permSibling = await Mesh.GetEffectivePermissions("OtherOrg", "InheritUser").Should().Match(p => p == Permission.None);

        permParent.Should().Be(Permission.All | Permission.Compile);
        permChild.Should().Be(Permission.All | Permission.Compile);
        permGrandchild.Should().Be(Permission.All | Permission.Compile);
        permSibling.Should().Be(Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_ExactMatch_TakesPrecedence()
    {
        var permOrg = await Mesh.GetEffectivePermissions("Org", "OverrideUser").Should().Match(p => p == Permission.None);
        var permSpecial = await Mesh.GetEffectivePermissions("Org/Special", "OverrideUser").Should().Match(p => p == (Permission.All | Permission.Compile));
        var permOther = await Mesh.GetEffectivePermissions("Org/Other", "OverrideUser").Should().Match(p => p == Permission.None);

        // OverrideUser_Org has Viewer at "Org"; OverrideUser has Admin at "Org/Special".
        // GetEffectivePermissions is called twice for two distinct user IDs (different objects).
        permOrg.Should().Be(Permission.None, "OverrideUser has no role at Org — only OverrideUser_Org does");
        permSpecial.Should().Be(Permission.All | Permission.Compile); // Admin at Org/Special
        permOther.Should().Be(Permission.None); // No role inherited (Viewer was for OverrideUser_Org, not OverrideUser)
    }

    #endregion

    #region Anonymous Access

    [Fact]
    public async Task GetEffectivePermissions_AnonymousNamespace_AnonymousHasReadAccess()
    {
        await Mesh.GetEffectivePermissions("MeshWeaver", "")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_CanAccessPublicNamespace()
    {
        // Admin context (DevLogin) is active for the seed creates.
        await SeedTopLevel(new MeshNode("PublicArea") { Name = "Public Area", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("Doc1", "PublicArea") { Name = "Document 1", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(new MeshNode("Doc2", "PublicArea") { Name = "Document 2", NodeType = "Group" }).Should().Emit();
        ClearAdminContext();

        var children = (await MeshQuery.Query<MeshNode>(new MeshQueryRequest { Query = "namespace:PublicArea", UserId = "" })
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count(n => n.NodeType != "AccessAssignment") == 2)).Items;

        var contentChildren = children.Where(n => n.NodeType != "AccessAssignment").ToList();
        contentChildren.Should().HaveCount(2);
        contentChildren.Should().Contain(n => n.Id == "Doc1");
        contentChildren.Should().Contain(n => n.Id == "Doc2");
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_CannotAccessPrivateNamespace()
    {
        await SeedTopLevel(new MeshNode("PrivateArea") { Name = "Private Area", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("Secret1", "PrivateArea") { Name = "Secret 1", NodeType = "Group" }).Should().Emit();
        ClearAdminContext();

        var children = (await MeshQuery.Query<MeshNode>(new MeshQueryRequest { Query = "namespace:PrivateArea", UserId = "" })
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        children.Should().BeEmpty();
    }

    [Fact]
    public async Task SecurePersistence_LoggedOutUser_SeesOnlyPublicRootChildren()
    {
        await SeedTopLevel(new MeshNode("OpenProject") { Name = "Open Project", NodeType = "Group" });
        await SeedTopLevel(new MeshNode("ClosedProject") { Name = "Closed Project", NodeType = "Group" });
        ClearAdminContext();

        var rootChildren = (await MeshQuery.Query<MeshNode>(new MeshQueryRequest { Query = "namespace:", UserId = "" })
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == "OpenProject"))).Items;

        rootChildren.Should().Contain(n => n.Id == "OpenProject");
        rootChildren.Should().NotContain(n => n.Id == "ClosedProject");
    }

    [Fact]
    public async Task GetEffectivePermissions_PrivateNamespace_AnonymousHasNoAccess()
    {
        await Mesh.GetEffectivePermissions("ACME", "")
            .Should().Match(p => p == Permission.None);
    }

    [Fact]
    public async Task GetEffectivePermissions_PublicChildOfPrivateParent_InheritsPublicFromChild()
    {
        var permPrivate = await Mesh.GetEffectivePermissions("Private", "").Should().Match(p => p == Permission.None);
        var permPublicDocs = await Mesh.GetEffectivePermissions("Private/PublicDocs", "")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));

        permPrivate.Should().Be(Permission.None);
        permPublicDocs.Should().Be(Permission.Read | Permission.Execute | Permission.Api);
    }

    #endregion

    #region Integration: Access + Anonymous/Public User

    [Fact]
    public async Task GetEffectivePermissions_AuthenticatedUser_HasAccessToPrivateNamespace()
    {
        var editorPerms = Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export | Permission.Compile;
        var permAnonymous = await Mesh.GetEffectivePermissions("Restricted", "").Should().Match(p => p == Permission.None);
        var permAuthUser = await Mesh.GetEffectivePermissions("Restricted", "AuthUser").Should().Match(p => p == editorPerms);

        permAnonymous.Should().Be(Permission.None);
        permAuthUser.Should().Be(editorPerms);
    }

    [Fact]
    public async Task AuthenticatedUser_InheritsPublicPermissions()
    {
        await Mesh.GetEffectivePermissions("PublicBase", "SomeAuthUser")
            .Should().Match(p => p.HasFlag(Permission.Read),
                "Authenticated users should inherit Public user permissions as a baseline");
    }

    [Fact]
    public async Task AnonymousUser_DoesNotInheritPublicPermissions()
    {
        var permAnon = await Mesh.GetEffectivePermissions("PublicOnly", "").Should().Match(p => p == Permission.None);
        permAnon.Should().Be(Permission.None,
            "Anonymous users should not inherit Public user permissions");

        var permAnonExplicit = await Mesh.GetEffectivePermissions("PublicOnly", WellKnownUsers.Anonymous)
            .Should().Match(p => p == Permission.None);
        permAnonExplicit.Should().Be(Permission.None,
            "Anonymous user explicitly should not inherit Public user permissions");
    }

    #endregion

    #region IMeshService Access Control Filtering

    [Fact]
    public async Task MeshQuery_AnonymousUser_CanQueryPublicOrganizations()
    {
        await SeedTopLevel(new MeshNode("Systemorph")
        {
            Name = "Systemorph",
            NodeType = "Group"
        });

        await SeedTopLevel(new MeshNode("ACME")
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
        var results = (await MeshQuery.Query<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Name == "Systemorph"))).Items;

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("Systemorph", "Anonymous-accessible namespace should be visible");
        nodeNames.Should().NotContain("ACME", "Private namespace should NOT be visible to anonymous");
    }

    [Fact]
    public async Task MeshQuery_WithAccessContext_CanQueryPublicOrganizations()
    {
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        await SeedTopLevel(new MeshNode("MeshWeaver2")
        {
            Name = "MeshWeaver2",
            NodeType = "Group"
        });

        await SeedTopLevel(new MeshNode("SecretOrg")
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
        var results = (await MeshQuery.Query<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Name == "MeshWeaver2"))).Items;

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("MeshWeaver2", "Anonymous-accessible namespace should be visible");
        nodeNames.Should().NotContain("SecretOrg", "Private namespace should NOT be visible to anonymous");
    }

    [Fact]
    public async Task MeshQuery_AnonymousUser_FiltersRestrictedNodes()
    {
        var publicNode = new MeshNode("PublicDoc", "Public") { Name = "Public Document", NodeType = "Group" };
        var restrictedNode = new MeshNode("PrivateDoc", "Private") { Name = "Private Document", NodeType = "Group" };

        await SeedTopLevel(new MeshNode("Public") { Name = "Public", NodeType = "Group" });
        await NodeFactory.CreateNode(publicNode).Should().Emit();
        await SeedTopLevel(new MeshNode("Private") { Name = "Private", NodeType = "Group" });
        await NodeFactory.CreateNode(restrictedNode).Should().Emit();
        ClearAdminContext();

        var request = new MeshQueryRequest { Query = "path:Public scope:descendants", UserId = "" };
        var publicResults = (await MeshQuery.Query<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Path == "Public/PublicDoc"))).Items;

        var restrictedRequest = new MeshQueryRequest { Query = "path:Private scope:descendants", UserId = "" };
        var restrictedResults = (await MeshQuery.Query<MeshNode>(restrictedRequest)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        var publicNodePaths = publicResults.OfType<MeshNode>().Select(n => n.Path).ToList();
        var restrictedNodePaths = restrictedResults.OfType<MeshNode>().Select(n => n.Path).ToList();

        publicNodePaths.Should().Contain("Public/PublicDoc");
        restrictedNodePaths.Should().NotContain("Private/PrivateDoc");
    }

    [Fact]
    public async Task MeshQuery_AuthenticatedUser_SeesRestrictedNodes()
    {
        var restrictedNode = new MeshNode("SecretDoc", "Secret") { Name = "Secret Document", NodeType = "Group" };
        await SeedTopLevel(new MeshNode("Secret") { Name = "Secret", NodeType = "Group" });
        await NodeFactory.CreateNode(restrictedNode).Should().Emit();
        ClearAdminContext();

        var request = new MeshQueryRequest { Query = "path:Secret scope:descendants", UserId = "QueryUser" };
        var results = (await MeshQuery.Query<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Path == "Secret/SecretDoc"))).Items;

        var nodePaths = results.OfType<MeshNode>().Select(n => n.Path).ToList();
        nodePaths.Should().Contain("Secret/SecretDoc");
    }

    [Fact]
    public async Task MeshQuery_PublicPermissions_NotPollutedByAdminContext()
    {
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        await SeedTopLevel(new MeshNode("PublicOrg3")
        {
            Name = "PublicOrg3",
            NodeType = "Code"
        });

        await SeedTopLevel(new MeshNode("PrivateOrg3")
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
        var results = (await MeshQuery.Query<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Name == "PublicOrg3"))).Items;

        var nodeNames = results.OfType<MeshNode>().Select(n => n.Name).ToList();
        nodeNames.Should().Contain("PublicOrg3", "Public namespace should be visible");
        nodeNames.Should().NotContain("PrivateOrg3",
            "Private namespace should NOT be visible to Public user, even when admin AccessContext is active");
    }

    [Fact]
    public async Task GetEffectivePermissions_PublicUser_IgnoresAdminAccessContextRoles()
    {
        var accessService = Mesh.ServiceProvider.GetService<AccessService>();

        accessService?.SetContext(new AccessContext
        {
            ObjectId = "AdminUser",
            Name = "Admin User",
            Roles = ["Admin"]
        });

        await Mesh.GetEffectivePermissions("ACME", WellKnownUsers.Public)
            .Should().Match(p => p == Permission.None,
                "Public user should not inherit Admin roles from AccessContext belonging to a different user");
    }

    #endregion

    #region Explicit Public Access Grants

    [Fact]
    public async Task SecurePersistence_NodeInUserNamespace_VisibleViaExplicitAnonymousGrant()
    {
        await SeedTopLevel(new MeshNode("Profiles") { Name = "Profiles", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("AliceProfile", "Profiles") { Name = "Alice Profile", NodeType = "Markdown" }).Should().Emit();
        ClearAdminContext();

        var children = (await MeshQuery.Query<MeshNode>(new MeshQueryRequest { Query = "namespace:Profiles", UserId = "" })
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == "AliceProfile"))).Items;

        children.Should().Contain(n => n.Id == "AliceProfile");
    }

    [Fact]
    public async Task SecurePersistence_NodeTypeDefinition_VisibleWithExplicitGrant()
    {
        await SeedTopLevel(new MeshNode("CustomType")
        {
            Name = "CustomType",
            NodeType = "NodeType"
        });

        await SeedTopLevel(new MeshNode("CustomInstance")
        {
            Name = "CustomInstance",
            NodeType = "Group"
        });
        ClearAdminContext();

        var typeDef = (await MeshQuery.Query<MeshNode>(new MeshQueryRequest { Query = "path:CustomType", UserId = "" })
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count > 0)).Items.FirstOrDefault();
        var instance = (await MeshQuery.Query<MeshNode>(new MeshQueryRequest { Query = "path:CustomInstance", UserId = "" })
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items.FirstOrDefault();

        typeDef.Should().NotBeNull("NodeType definitions are readable with explicit Anonymous Viewer grant");
        typeDef!.Name.Should().Be("CustomType");
        instance.Should().BeNull("Non-granted instances require explicit access grants");
    }

    [Fact]
    public async Task SecurePersistence_NodeInPrivateNamespace_HiddenWithoutGrant()
    {
        await SeedTopLevel(new MeshNode("SecretArea") { Name = "Secret Area", NodeType = "Group" });
        await NodeFactory.CreateNode(new MeshNode("Doc1", "SecretArea") { Name = "Secret Doc", NodeType = "Group" }).Should().Emit();
        ClearAdminContext();

        var children = (await MeshQuery.Query<MeshNode>(new MeshQueryRequest { Query = "namespace:SecretArea", UserId = "" })
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

        children.Should().BeEmpty();
    }

    #endregion
}
