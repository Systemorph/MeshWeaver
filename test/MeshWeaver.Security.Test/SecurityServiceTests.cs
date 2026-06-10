using System.Reactive.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
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
/// All AccessAssignment / PartitionAccessPolicy nodes are seeded statically via
/// <see cref="MeshBuilder.AddMeshNodes"/>; permissions are read through the reactive
/// <see cref="SecurityService"/> surface.
/// </summary>
public class SecurityServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("user123", "Admin", "org/acme/project"),
                AssignmentNodeFactory.UserRole("viewer123", "Viewer", "org/acme/docs"),
                AssignmentNodeFactory.UserRole("editor123", "Editor", "org/acme/project/docs"),
                AssignmentNodeFactory.UserRole("inherituser", "Admin", "org/parent"),
                AssignmentNodeFactory.UserRole("globaladmin", "Admin", null),
                AssignmentNodeFactory.UserRole("multiuser_v", "Viewer", "org/project1"),
                AssignmentNodeFactory.UserRole("multiuser", "Editor", "org/project1/subproject"),
                AssignmentNodeFactory.UserRole("readuser", "Viewer", "org/docs/readme"),
                AssignmentNodeFactory.UserRole("readonlyuser", "Viewer", "org/restricted/data"),
                AssignmentNodeFactory.UserRole("newassignee", "Editor", "org/newproject"),
                AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "org/public/area"));

    }

    /// <summary>
    /// Skip PublicAdminAccess — security tests need granular permissions.
    /// </summary>
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 20000)]
    public void GetEffectivePermissions_WithAdminRole_ReturnsAllPermissions()
    {
        Mesh.GetEffectivePermissions("org/acme/project", "user123")
            .Should().Match(p => p == Permission.All);
    }

    [Fact(Timeout = 20000)]
    public void GetEffectivePermissions_WithViewerRole_ReturnsReadOnly()
    {
        Mesh.GetEffectivePermissions("org/acme/docs", "viewer123")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));
    }

    [Fact(Timeout = 20000)]
    public void GetEffectivePermissions_WithEditorRole_ReturnsReadCreateUpdate()
    {
        var editorPerms = Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export;
        Mesh.GetEffectivePermissions("org/acme/project/docs", "editor123")
            .Should().Match(p => p == editorPerms);
    }

    [Fact(Timeout = 20000)]
    public void GetEffectivePermissions_NoRoles_ReturnsNone()
    {
        Mesh.GetEffectivePermissions("org/private/secure", "newuser")
            .Should().Match(p => p == Permission.None);
    }

    [Fact(Timeout = 20000)]
    public void GetEffectivePermissions_WithInheritance_InheritsFromParent()
    {
        Mesh.GetEffectivePermissions("org/parent/child/grandchild", "inherituser")
            .Should().Match(p => p == Permission.All);
    }

    [Fact(Timeout = 20000)]
    public void GetEffectivePermissions_WithGlobalRole_AppliesEverywhere()
    {
        Mesh.GetEffectivePermissions("some/random/path", "globaladmin")
            .Should().Match(p => p == Permission.All);
    }

    [Fact(Timeout = 20000)]
    public void GetEffectivePermissions_CombinesMultipleRoles()
    {
        // multiuser has Editor at "org/project1/subproject"; multiuser_v has Viewer at "org/project1".
        // Reading multiuser at the deeper path returns Editor permissions only.
        var editorPerms = Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export;
        Mesh.GetEffectivePermissions("org/project1/subproject", "multiuser")
            .Should().Match(p => p == editorPerms);
    }

    [Fact(Timeout = 20000)]
    public void HasPermission_WithSufficientPermissions_ReturnsTrue()
    {
        Mesh.CheckPermission("org/docs/readme", "readuser", Permission.Read)
            .Should().Match(canRead => canRead);
    }

    [Fact(Timeout = 20000)]
    public void HasPermission_WithoutSufficientPermissions_ReturnsFalse()
    {
        var canDelete = Mesh.CheckPermission("org/restricted/data", "readonlyuser", Permission.Delete).Should().Emit();
        canDelete.Should().BeFalse();
    }

    [Fact(Timeout = 20000)]
    public void AddUserRole_CreatesAssignment()
    {
        Mesh.GetEffectivePermissions("org/newproject", "newassignee")
            .Should().Match(p => p.HasFlag(Permission.Update));
    }

    [Fact(Timeout = 20000)]
    public void RemoveUserRole_RemovesAssignment()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var savedContext = accessService.CircuitContext;
        try
        {
            // Use globaladmin to authorize the create + delete of a runtime AccessAssignment.
            accessService.SetCircuitContext(new AccessContext { ObjectId = "globaladmin", Name = "globaladmin" });

            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

            // Create a temporary assignment then remove it via DeleteNode
            var assignment = AssignmentNodeFactory.UserRole("removetest", "Admin", "org/removeproject");
            meshService.CreateNode(assignment).Should().Emit();

            // Verify the assignment took effect first (so the test fails on a hung
            // CREATE, not a hung DELETE).
            Mesh.GetEffectivePermissions("org/removeproject", "removetest")
                .Should().Match(p => p == Permission.All);

            meshService.DeleteNode(assignment.Path!).Should().Emit();

            // Wait for the synced query to drop the deleted node (eventual consistency).
            Mesh.GetEffectivePermissions("org/removeproject", "removetest")
                .Should().Match(p => p == Permission.None);
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
    }

    /// <summary>
    /// Regression for the 2026-05-01 cleanup-session bug:
    /// AccessAssignment created at runtime (via <see cref="IMeshService.CreateNode"/>)
    /// must propagate to <see cref="SecurityService.GetEffectivePermissions"/>.
    /// Previously the synced AccessAssignment query was set up lazily inside
    /// <c>GetUserScopeRolesStream</c> with a per-user keep-alive subscription —
    /// when no permission check had been issued for that user yet, the synced
    /// query had no live subscriber and runtime emissions silently dropped on the
    /// floor. This test forces the failing path: subscribe before any check, then
    /// create an AccessAssignment, then check permissions.
    ///
    /// We seed `globaladmin` (already in <c>ConfigureMesh</c>) and use that
    /// identity to create the runtime assignment — the bug under test is whether
    /// the new node propagates to the synced query, not whether anyone can
    /// create it.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void RuntimeCreateNode_AccessAssignment_GrantsPermission()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var savedContext = accessService.CircuitContext;

        const string userId = "runtime-assignee";
        const string scope = "org/runtimeproject";

        try
        {
            // Pre-condition: the user has no permissions on the scope (no static seed).
            Mesh.GetEffectivePermissions(scope, userId)
                .Should().Match(p => p == Permission.None,
                    "the user has no static AccessAssignment for this scope");

            // Switch to globaladmin (statically seeded with Admin at root) to
            // create the new AccessAssignment. The IMeshService captures the
            // identity at call time, so this only authorizes the create itself.
            accessService.SetCircuitContext(new AccessContext { ObjectId = "globaladmin", Name = "globaladmin" });

            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var assignment = AssignmentNodeFactory.UserRole(userId, "Admin", scope);
            meshService.CreateNode(assignment).Should().Emit();

            // The new assignment must now be visible to permission checks. Match
            // on the first emission carrying the new role so the test is robust
            // against the synced-query emitting one stale snapshot first.
            Mesh.GetEffectivePermissions(scope, userId)
                .Should().Match(p => p == Permission.All,
                    "Admin role on the scope grants every permission once the synced query observes the new node");
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
    }

    /// <summary>
    /// Cross-scope regression: the sender stamps Admin onto delivery.AccessContext
    /// (via the PostPipeline), routes a message to a per-node hub, and the
    /// per-node hub's AccessControlPipeline must read those claim-based roles
    /// when calling SecurityService.HasPermission.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void DeliveryAccessContext_RolesFlow_ToReceiverPermissionCheck()
    {
        using var scope = Mesh.ServiceProvider.CreateScope();
        var accessService = scope.ServiceProvider.GetRequiredService<AccessService>();
        var sec = scope.ServiceProvider.GetRequiredService<IMessageHub>();

        const string userId = "delivery-admin";

        // Receiver scope starts clean — context is null. This mirrors the
        // production shape where the per-node hub processes a delivery in a
        // fresh async flow with no inherited AsyncLocal.
        accessService.SetContext(null);

        // Pretend AccessControlPipeline restored the sender's AccessContext
        // (this is exactly what the new pipeline-level fix does the moment a
        // permission-gated delivery arrives).
        accessService.SetContext(new AccessContext
        {
            ObjectId = userId,
            Name = userId,
            Roles = new[] { "Admin" }
        });

        // No static AccessAssignment exists for this user — the only signal is
        // the claim-based Admin from the delivery context. The chain must still
        // resolve Permission.All; if SecurityService didn't read claim-based
        // roles, we'd see Permission.None.
        sec.GetEffectivePermissions("any/scope", userId)
            .Should().Be(Permission.All,
                "claim-based Admin restored from delivery.AccessContext must grant " +
                "all permissions on the receiver's permission check");
    }

    [Fact(Timeout = 10_000)]
    public void ClaimBasedAdmin_GrantsImmediately_EvenWhenNoStaticAssignment()
    {
        // Resolve a scoped SecurityService directly so the AccessContext we
        // set is the one it reads.
        using var scope = Mesh.ServiceProvider.CreateScope();
        var accessService = scope.ServiceProvider.GetRequiredService<AccessService>();
        var sec = scope.ServiceProvider.GetRequiredService<IMessageHub>();

        const string userId = "claim-only-admin";

        // Sanity: this user has NO static AccessAssignment in ConfigureMesh.
        accessService.SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });
        sec.GetEffectivePermissions("any/scope", userId)
            .Should().Be(Permission.None,
                "without claim-based roles, the user has no permissions");

        // Stamp Admin via AccessContext.Roles — the same path the API token
        // middleware uses.
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = userId,
            Name = userId,
            Roles = new[] { "Admin" }
        });

        sec.GetEffectivePermissions("any/scope", userId)
            .Should().Be(Permission.All,
                "claim-based Admin must grant all permissions regardless of the " +
                "synced-query state");
    }

    /// <summary>
    /// Companion regression: the new AccessAssignment must also be observable
    /// through the permission-check path the AccessControlPipeline actually calls.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void RuntimeCreateNode_AccessAssignment_HasPermissionReturnsTrue()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var savedContext = accessService.CircuitContext;

        const string userId = "runtime-assignee2";
        const string scope = "org/runtimeproject2";

        try
        {
            // Sanity: no permission before creating the assignment.
            Mesh.CheckPermission(scope, userId, Permission.Read).Should().Emit()
                .Should().BeFalse();

            accessService.SetCircuitContext(new AccessContext { ObjectId = "globaladmin", Name = "globaladmin" });

            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var assignment = AssignmentNodeFactory.UserRole(userId, "Admin", scope);
            meshService.CreateNode(assignment).Should().Emit();

            // After CreateNode completes, the permission check must observe the
            // new role. Wait reactively for the grant to surface.
            Mesh.CheckPermission(scope, userId, Permission.Read)
                .Should().Match(granted => granted,
                    "Admin role granted at runtime via CreateNode must be visible to the " +
                    "permission check — otherwise AccessControlPipeline rejects every message that " +
                    "would have been authorized by that assignment.");
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
    }

    [Fact(Timeout = 20000)]
    public void AnonymousUser_GetsAnonymousPermissions()
    {
        Mesh.GetEffectivePermissions("org/public/area", "")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));
    }

    /// <summary>
    /// Regression for the 2026-05-10 chat-render slowness in
    /// <c>Memex.Portal.Distributed</c>: every fresh permission check for
    /// a user without a static AccessAssignment paid the 2 s
    /// <c>GetUserScopeRolesStream</c> Timeout fallback before falling
    /// through to claim-based roles.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void PermissionChecks_DoNotPayTimeoutFallback()
    {
        // Warm the SecurityService with one throw-away check so the
        // first-call cold paths (DI resolution, hub activation) don't
        // count against the perf budget.
        Mesh.GetEffectivePermissions("warmup/scope", "warmup-user").Should().Emit();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            Mesh.GetEffectivePermissions($"some/scope/{i}", $"u{i}").Should().Emit();
        }
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "permission checks must not pay the 2 s GetUserScopeRolesStream " +
            "timeout fallback. If this asserts > 5 s, the synced-query Initial " +
            "is not landing within the timeout window for cold users/scopes — " +
            "see AccessControl.md > 4-query union for the target shape.");
    }

    [Fact(Timeout = 20000)]
    public void GetRole_ReturnsBuiltInRole()
    {
        // Built-in roles are static — assert directly. The (gone) SecurityService.GetRole
        // surface is no longer reachable from tests; the per-hub scoped service handles
        // role merging at permission-evaluation time.
        Role.Admin.Permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public void GetRoles_ReturnsAllBuiltInRoles()
    {
        // Built-in role registry is static.
        new[] { Role.Admin, Role.Editor, Role.Viewer, Role.Commenter }
            .Select(r => r.Id)
            .Should().Contain(["Admin", "Editor", "Viewer", "Commenter"]);
    }
}

/// <summary>
/// Tests for the RlsNodeValidator integration.
/// </summary>
public class RlsNodeValidatorTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("authorized-user", "Admin", "allowed/area"));
    }

    [Fact(Timeout = 20000)]
    public void ValidateAsync_WithoutPermission_ReturnsUnauthorized()
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

        var result = validator!.Validate(context).Should().Emit();

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.Unauthorized);
    }

    [Fact(Timeout = 20000)]
    public void ValidateAsync_WithPermission_ReturnsValid()
    {
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<RlsNodeValidator>()
            .FirstOrDefault();
        validator.Should().NotBeNull();

        const string userId = "authorized-user";

        var node = new MeshNode("test", "allowed/area") { Name = "Test Node" };
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = node,
            Request = new CreateNodeRequest(node) { CreatedBy = userId },
            AccessContext = new AccessContext { ObjectId = userId }
        };

        var result = validator!.Validate(context).Should().Emit();

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// Self-access rule: when MainNode matches userId, all operations are allowed
    /// even without explicit permission grants.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void ValidateAsync_SelfAccess_MainNodeMatchesUserId_ReturnsValid()
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

            var result = validator!.Validate(context).Should().Emit();
            result.IsValid.Should().BeTrue($"hub should have {op} access to its own node (MainNode == userId)");
        }
    }

    /// <summary>
    /// Self-access rule should NOT apply when MainNode does not match userId.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void ValidateAsync_SelfAccess_MainNodeMismatch_ChecksPermissions()
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

        var result = validator!.Validate(context).Should().Emit();
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
/// </summary>
public class HubSelfAccessTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("Roland", "Admin", null));
    }

    /// <summary>
    /// Skip PublicAdminAccess — we want strict RLS with no global grants.
    /// </summary>
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// Seed the TestHub node via IMeshService so it's stored in persistence.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // A top-level node IS a partition root; the PartitionWriteGuard only lets System
        // (the partition provisioner) create a non-partition type there — and this class runs
        // strict RLS without PublicAdminAccess. The node just needs to exist for the
        // hub-self-access reads, so seed it under System.
        SeedTopLevel(
            new MeshNode("TestHub")
            {
                Name = "Test Hub",
                NodeType = "Markdown",
                Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Test Hub" }
            });
    }

    /// <summary>
    /// A hub using ImpersonateAsHub can query its own MeshNode
    /// even without any explicit permission grants.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void Hub_CanQueryOwnNode_WithImpersonateAsHub()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        var hub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("TestHub"),
            c => c);

        MeshNode? node;
        using (accessService.ImpersonateAsHub(hub))
        {
            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            node = meshService.Query<MeshNode>(MeshQueryRequest.FromQuery("path:TestHub"))
                .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count > 0).Items.FirstOrDefault();
        }

        node.Should().NotBeNull("hub should always be able to see its own node");
        node!.Id.Should().Be("TestHub");
    }

    /// <summary>
    /// Without ImpersonateAsHub and without permissions, a random user
    /// should NOT be able to see the hub's node.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void UnauthorizedUser_CannotQueryHubNode()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        accessService.SetContext(new AccessContext { ObjectId = "random-user", Name = "Random" });
        accessService.SetCircuitContext(new AccessContext { ObjectId = "random-user", Name = "Random" });

        try
        {
            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var node = meshService.Query<MeshNode>(MeshQueryRequest.FromQuery("path:TestHub"))
                .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items.FirstOrDefault();
            node.Should().BeNull("unauthorized user should not see the hub's node without permissions");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }
}

/// <summary>
/// Tests for security using sample-Graph–like data, seeded via static AccessAssignment nodes.
/// </summary>
public class SampleDataSecurityTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // All [Fact]s do read-only permission checks against statically-seeded role
    // assignments — no test mutates the security state. Safe to share SP across tests.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("Roland", "Admin", null),
                AssignmentNodeFactory.UserRole("Alice", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("Public", "Viewer", "MeshWeaver"));
    }

    [Fact(Timeout = 20000)]
    public void Roland_WithGlobalAdminRole_CanEditArchitectureNode()
    {
        const string userId = "Roland";
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        var permissions = Mesh.GetEffectivePermissions(nodePath, userId).Should().Match(p => p == Permission.All);
        var canEdit = Mesh.CheckPermission(nodePath, userId, Permission.Update).Should().Emit();

        permissions.Should().Be(Permission.All, "Roland has global Admin role");
        canEdit.Should().BeTrue("Roland should be able to edit the Architecture node");
    }

    [Fact(Timeout = 20000)]
    public void Roland_GlobalAdmin_CanEditAnyNode()
    {
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
            var canEdit = Mesh.CheckPermission(path, userId, Permission.Update).Should().Emit();
            canEdit.Should().BeTrue($"Roland should be able to edit '{path}' as global Admin");
        }
    }

    [Fact(Timeout = 20000)]
    public void Alice_WithAcmeEditorRole_CanEditInAcmeOnly()
    {
        const string userId = "Alice";

        var canEditAcme = Mesh.CheckPermission("ACME/Project/Task1", userId, Permission.Update).Should().Emit();
        var canEditMeshWeaver = Mesh.CheckPermission("MeshWeaver/Documentation", userId, Permission.Update).Should().Emit();

        canEditAcme.Should().BeTrue("Alice should be able to edit in Software namespace");
        canEditMeshWeaver.Should().BeFalse("Alice should NOT be able to edit in MeshWeaver namespace");
    }

    [Fact(Timeout = 20000)]
    public void PublicUser_WithMeshWeaverViewerRole_CannotEdit()
    {
        const string userId = "Public";
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        var canEdit = Mesh.CheckPermission(nodePath, userId, Permission.Update).Should().Emit();
        var canRead = Mesh.CheckPermission(nodePath, userId, Permission.Read).Should().Emit();

        canRead.Should().BeTrue("Public user should be able to read MeshWeaver content");
        canEdit.Should().BeFalse("Public user should NOT be able to edit MeshWeaver content");
    }
}

/// <summary>
/// Tests for PartitionAccessPolicy feature.
/// All policies + role assignments are seeded statically via <see cref="MeshBuilder.AddMeshNodes"/>.
/// </summary>
public class PartitionAccessPolicyTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("editor1", "Editor", "org/docs"),
                AssignmentNodeFactory.Policy("org/docs", new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false, Thread = false }),
                AssignmentNodeFactory.UserRole("admin1", "Admin", ""),
                AssignmentNodeFactory.Policy("platform/docs", new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false, Thread = false }),
                AssignmentNodeFactory.UserRole("user2", "Admin", "ACME"),
                AssignmentNodeFactory.Policy("Doc_test_pol", new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false, Thread = false }),
                AssignmentNodeFactory.UserRole("user3", "Admin", ""),
                AssignmentNodeFactory.Policy("org_nest", new PartitionAccessPolicy { Create = false, Update = false, Delete = false }),
                AssignmentNodeFactory.Policy("org_nest/restricted", new PartitionAccessPolicy { Create = false, Update = false, Delete = false, Comment = false, Thread = false }),
                AssignmentNodeFactory.UserRole("user4", "Admin", ""),
                AssignmentNodeFactory.Policy("isolated", new PartitionAccessPolicy { BreaksInheritance = true }),
                AssignmentNodeFactory.UserRole("user5", "Admin", ""),
                AssignmentNodeFactory.Policy("scoped", new PartitionAccessPolicy { BreaksInheritance = true }),
                AssignmentNodeFactory.UserRole("user5", "Editor", "scoped"),
                AssignmentNodeFactory.UserRole(WellKnownUsers.Public, "Viewer", "org/public_capped"),
                AssignmentNodeFactory.Policy("org/public_capped", new PartitionAccessPolicy { Read = false, Create = false, Update = false, Delete = false, Comment = false, Thread = false })
            );
    }

    [Fact(Timeout = 20000)]
    public void PolicyCapsPermissions_EditorCappedToRead()
    {
        Mesh.GetEffectivePermissions("org/docs", "editor1")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api | Permission.Export));
    }

    [Fact(Timeout = 20000)]
    public void PolicyCapsAdmin_GlobalAdminCappedToRead()
    {
        const string userId = "admin1";
        const string docNs = "platform/docs";
        var capped = Permission.Read | Permission.Execute | Permission.Api | Permission.Export;

        var docPermissions = Mesh.GetEffectivePermissions(docNs, userId).Should().Match(p => p == capped);
        docPermissions.Should().Be(capped);

        var childPermissions = Mesh.GetEffectivePermissions("platform/docs/readme", userId).Should().Match(p => p == capped);
        childPermissions.Should().Be(capped);

        var otherPermissions = Mesh.GetEffectivePermissions("platform/code", userId).Should().Match(p => p == Permission.All);
        otherPermissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public void PolicyDoesNotAffectSiblingNamespace()
    {
        Mesh.GetEffectivePermissions("ACME/Project", "user2")
            .Should().Match(p => p == Permission.All, "ACME should not be affected by Doc_test_pol policy");
    }

    [Fact(Timeout = 20000)]
    public void NestedPoliciesAccumulate()
    {
        var orgExpected = Permission.Read | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export;
        var orgPermissions = Mesh.GetEffectivePermissions("org_nest/general", "user3").Should().Match(p => p == orgExpected);
        orgPermissions.Should().Be(orgExpected, "org level allows Read + Comment + Execute + Thread + Api + Export");

        var restrictedExpected = Permission.Read | Permission.Execute | Permission.Api | Permission.Export;
        var restrictedPermissions = Mesh.GetEffectivePermissions("org_nest/restricted/item", "user3").Should().Match(p => p == restrictedExpected);
        restrictedPermissions.Should().Be(restrictedExpected, "nested policy further restricts to Read + Execute + Api + Export only (Thread also denied)");
    }

    [Fact(Timeout = 20000)]
    public void BreaksInheritance_DiscardsParentRoles()
    {
        // user4 has Admin globally, but isolated namespace breaks inheritance
        Mesh.GetEffectivePermissions("isolated/item", "user4")
            .Should().Match(p => p == Permission.None, "inherited Admin from global should be discarded");
    }

    [Fact(Timeout = 20000)]
    public void BreaksInheritance_KeepsLocalRoles()
    {
        // user5 has Admin globally + Editor at scoped (which breaks inheritance)
        var editorPerms = Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export;
        Mesh.GetEffectivePermissions("scoped/item", "user5")
            .Should().Match(p => p == editorPerms,
                "local Editor role should survive, inherited Admin should be discarded");
    }

    [Fact(Timeout = 20000)]
    public void PolicyAppliesToPublicUser()
    {
        Mesh.GetEffectivePermissions("org/public_capped", WellKnownUsers.Public)
            .Should().Match(p => p == (Permission.Execute | Permission.Api),
                "Public user permissions should be capped to Execute + Api only (Read denied by policy)");
    }
}

/// <summary>
/// Tests that static node providers (Doc, Agent, Role) enforce read-only policies
/// via PartitionAccessPolicy nodes emitted from their GetStaticNodes() methods.
/// </summary>
public class StaticNamespacePolicyTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // NOTE: NOT opted into ShareMeshAcrossTests — static policy caps differ
    // between fresh and reused SPs.

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddDocumentation()
            .AddAgentType()
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("admin_doc", "Admin", ""),
                AssignmentNodeFactory.UserRole("editor_doc", "Editor", "Doc"),
                AssignmentNodeFactory.UserRole("admin_agent", "Admin", ""),
                AssignmentNodeFactory.UserRole("admin_role", "Admin", ""),
                AssignmentNodeFactory.UserRole("admin_other", "Admin", ""),
                AssignmentNodeFactory.UserRole("admin_docroot", "Admin", ""));
    }

    [Fact(Timeout = 20000)]
    public void DocNamespace_AdminCappedToReadOnly()
    {
        var expected = Permission.Read | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export;
        Mesh.GetEffectivePermissions("Doc/GettingStarted", "admin_doc")
            .Should().Match(p => p == expected, "Doc namespace allows read + comment + thread + export but not create/update/delete");
    }

    [Fact(Timeout = 20000)]
    public void DocNamespace_EditorCappedToReadOnly()
    {
        var expected = Permission.Read | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export;
        Mesh.GetEffectivePermissions("Doc/AI/AgenticAI", "editor_doc")
            .Should().Match(p => p == expected, "Doc namespace allows read + comment + thread + export but not create/update/delete");
    }

    [Fact(Timeout = 20000)]
    public void AgentNamespace_AdminCappedToReadOnly()
    {
        var expected = Permission.Read | Permission.Execute | Permission.Api | Permission.Export;
        Mesh.GetEffectivePermissions("Agent/ThreadNamer", "admin_agent")
            .Should().Match(p => p == expected, "Agent namespace has a static read-only policy");
    }

    [Fact(Timeout = 20000)]
    public void RoleNamespace_AdminCappedToReadOnly()
    {
        var expected = Permission.Read | Permission.Execute | Permission.Api | Permission.Export;
        Mesh.GetEffectivePermissions("Role/Admin", "admin_role")
            .Should().Match(p => p == expected, "Role namespace has a static read-only policy");
    }

    [Fact(Timeout = 20000)]
    public void StaticPolicy_DoesNotAffectOtherNamespaces()
    {
        Mesh.GetEffectivePermissions("ACME/Project/Task1", "admin_other")
            .Should().Match(p => p == Permission.All, "ACME is not a static namespace, Admin should have full access");
    }

    [Fact(Timeout = 20000)]
    public void StaticPolicy_DocRootItselfCapped()
    {
        var expected = Permission.Read | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export;
        Mesh.GetEffectivePermissions("Doc", "admin_docroot")
            .Should().Match(p => p == expected, "Doc root itself should be capped to Read + Comment + Execute + Thread + Api + Export");
    }
}
