using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
/// <see cref="SecurityService"/> surface bridged via <c>.FirstAsync().ToTask(ct)</c>.
/// </summary>
public class SecurityServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Combined token: 10-second wall-clock cap AND the test runner's cancellation
    // (Ctrl+C / `dotnet test --cancel`). Without the linked context token, a hung
    // test would only release after the full 10 s even when the runner asked it
    // to stop sooner.
    private CancellationToken TestTimeout => CancellationTokenSource.CreateLinkedTokenSource(
        new CancellationTokenSource(10.Seconds()).Token,
        TestContext.Current.CancellationToken).Token;

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
    public async Task GetEffectivePermissions_WithAdminRole_ReturnsAllPermissions()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("org/acme/project", "user123", TestTimeout);

        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithViewerRole_ReturnsReadOnly()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("org/acme/docs", "viewer123", TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithEditorRole_ReturnsReadCreateUpdate()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("org/acme/project/docs", "editor123", TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_NoRoles_ReturnsNone()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("org/private/secure", "newuser", TestTimeout);

        permissions.Should().Be(Permission.None);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithInheritance_InheritsFromParent()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("org/parent/child/grandchild", "inherituser", TestTimeout);

        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_WithGlobalRole_AppliesEverywhere()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("some/random/path", "globaladmin", TestTimeout);

        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task GetEffectivePermissions_CombinesMultipleRoles()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // multiuser has Editor at "org/project1/subproject"; multiuser_v has Viewer at "org/project1".
        // Reading multiuser at the deeper path returns Editor permissions only.
        var permissions = await Mesh.GetPermissionAsync("org/project1/subproject", "multiuser", TestTimeout);

        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export);
    }

    [Fact(Timeout = 20000)]
    public async Task HasPermission_WithSufficientPermissions_ReturnsTrue()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var canRead = await Mesh.HasPermissionAsync("org/docs/readme", "readuser", Permission.Read, TestTimeout);
        canRead.Should().BeTrue();
    }

    [Fact(Timeout = 20000)]
    public async Task HasPermission_WithoutSufficientPermissions_ReturnsFalse()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var canDelete = await Mesh.HasPermissionAsync("org/restricted/data", "readonlyuser", Permission.Delete, TestTimeout);
        canDelete.Should().BeFalse();
    }

    [Fact(Timeout = 20000)]
    public async Task AddUserRole_CreatesAssignment()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("org/newproject", "newassignee", TestTimeout);
        permissions.Should().HaveFlag(Permission.Update);
    }

    [Fact(Timeout = 20000)]
    public async Task RemoveUserRole_RemovesAssignment()
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
            await meshService.CreateNode(assignment).FirstAsync().ToTask(TestTimeout);

            // Verify the assignment took effect first (so the test fails on a hung
            // CREATE, not a hung DELETE).
            var afterCreate = await Mesh.GetPermissionAsync("org/removeproject", "removetest",
                until: p => p.HasFlag(Permission.Read), TestTimeout);
            afterCreate.Should().Be(Permission.All);

            await meshService.DeleteNode(assignment.Path!).FirstAsync().ToTask(TestTimeout);

            // Wait for the synced query to drop the deleted node (eventual consistency).
            var afterDelete = await Mesh.GetPermissionAsync("org/removeproject", "removetest",
                until: p => p == Permission.None, TestTimeout);
            afterDelete.Should().Be(Permission.None);
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
    public async Task RuntimeCreateNode_AccessAssignment_GrantsPermission()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var savedContext = accessService.CircuitContext;

        const string userId = "runtime-assignee";
        const string scope = "org/runtimeproject";

        try
        {
            // Pre-condition: the user has no permissions on the scope (no static seed).
            var before = await Mesh.GetPermissionAsync(scope, userId, TestTimeout);
            before.Should().Be(Permission.None,
                "the user has no static AccessAssignment for this scope");

            // Switch to globaladmin (statically seeded with Admin at root) to
            // create the new AccessAssignment. The IMeshService captures the
            // identity at call time, so this only authorizes the create itself.
            accessService.SetCircuitContext(new AccessContext { ObjectId = "globaladmin", Name = "globaladmin" });

            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var assignment = AssignmentNodeFactory.UserRole(userId, "Admin", scope);
            await meshService.CreateNode(assignment).FirstAsync().ToTask(TestTimeout);

            // The new assignment must now be visible to permission checks. Use
            // the wait-for-predicate overload so the test is robust against the
            // synced-query emitting one stale snapshot before the new assignment
            // lands — but bounded at 5 s so a real regression fails loudly.
            var after = await Mesh.GetPermissionAsync(scope, userId,
                until: p => p.HasFlag(Permission.Read), TestTimeout);
            after.Should().Be(Permission.All,
                "Admin role on the scope grants every permission once the synced query observes the new node");
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
    }

    /// <summary>
    /// Regression for the 2026-05-01 distributed-mode permission denial.
    /// When the synced AccessAssignment query is wedged (or simply slow on
    /// first call) and never produces a first emission inside its replay
    /// buffer, every permission check used to block until the
    /// AccessControlPipeline timed out at 10 s and failed closed — even when
    /// the requesting user's bearer token carried <c>Roles=["Admin"]</c>
    /// claim that should have authorised the call regardless of the
    /// assignment lookup. The fix puts a 2 s timeout on the
    /// <c>GetUserScopeRolesStream</c> upstream so the chain falls back to
    /// "no synced roles" and folds in <c>AccessContext.Roles</c> via the
    /// claim-based path.
    ///
    /// <para>This test exercises a user that has NO static AccessAssignment
    /// at all (so the synced-query path returns an empty dict for them),
    /// stamps an Admin role onto the AccessContext via
    /// <see cref="AccessService.SetCircuitContext"/> the way the API token
    /// middleware does, and asserts the call completes with
    /// <see cref="Permission.All"/> within 5 s — well under the
    /// AccessControlPipeline's 10 s deadline.</para>
    /// </summary>
    /// <summary>
    /// Cross-scope regression: the sender stamps Admin onto delivery.AccessContext
    /// (via the PostPipeline), routes a message to a per-node hub, and the
    /// per-node hub's AccessControlPipeline must read those claim-based roles
    /// when calling SecurityService.HasPermission. Before the fix, the receiver
    /// only saw the userId — not the Roles — because AccessService.Context was
    /// null in the receiver's scope, leaving the claim-based grant path with
    /// nothing to fold in. Every API-token request that didn't have a static
    /// AccessAssignment was denied as a result.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task DeliveryAccessContext_RolesFlow_ToReceiverPermissionCheck()
    {
        using var scope = Mesh.ServiceProvider.CreateScope();
        var accessService = scope.ServiceProvider.GetRequiredService<AccessService>();
        var sec = scope.ServiceProvider.GetRequiredService<SecurityService>();

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
        var perms = await sec.GetEffectivePermissions("any/scope", userId)
            .FirstAsync().ToTask(TestTimeout);
        perms.Should().Be(Permission.All,
            "claim-based Admin restored from delivery.AccessContext must grant " +
            "all permissions on the receiver's permission check");
    }

    [Fact(Timeout = 10_000)]
    public async Task ClaimBasedAdmin_GrantsImmediately_EvenWhenNoStaticAssignment()
    {
        // Resolve a scoped SecurityService directly so the AccessContext we
        // set is the one it reads — the test helper GetPermissionAsync
        // creates its own scope and resets the AccessContext, dropping any
        // Roles we'd set. The API token middleware in production stamps
        // Roles onto the SAME scope where SecurityService runs.
        using var scope = Mesh.ServiceProvider.CreateScope();
        var accessService = scope.ServiceProvider.GetRequiredService<AccessService>();
        var sec = scope.ServiceProvider.GetRequiredService<SecurityService>();

        const string userId = "claim-only-admin";

        // Sanity: this user has NO static AccessAssignment in ConfigureMesh.
        // Without claim-based roles the synced-query / static-seed path
        // produces an empty role set for them.
        accessService.SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });
        var bareline = await sec.GetEffectivePermissions("any/scope", userId)
            .FirstAsync().ToTask(TestTimeout);
        bareline.Should().Be(Permission.None,
            "without claim-based roles, the user has no permissions");

        // Stamp Admin via AccessContext.Roles — the same path the API token
        // middleware uses (UserContextMiddleware copies the validated token's
        // Roles into AccessContext.Roles). The synced-query / static-seed
        // pipeline still produces nothing for this user; the Admin grant has
        // to flow purely through the claim-based code path inside
        // GetEffectivePermissions.
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = userId,
            Name = userId,
            Roles = new[] { "Admin" }
        });

        // Must resolve well under the AccessControlPipeline's 10 s deadline.
        // With the synced-query timeout fallback, total wait is bounded by
        // 2 s (timeout) + role compute (synchronous). Without the fallback
        // a wedged synced query would hang the whole 10 s.
        var perms = await sec.GetEffectivePermissions("any/scope", userId)
            .FirstAsync().ToTask(TestTimeout);
        perms.Should().Be(Permission.All,
            "claim-based Admin must grant all permissions regardless of the " +
            "synced-query state");
    }

    /// <summary>
    /// Companion regression: the new AccessAssignment must also be observable
    /// through <see cref="SecurityService.HasPermission(string, string, Permission)"/> —
    /// the path the AccessControlPipeline actually calls. (The bug surfaced as
    /// MCP <c>compile</c> being denied because HasPermission returned false even
    /// though the AccessAssignment row existed in the database.)
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task RuntimeCreateNode_AccessAssignment_HasPermissionReturnsTrue()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var savedContext = accessService.CircuitContext;

        const string userId = "runtime-assignee2";
        const string scope = "org/runtimeproject2";

        try
        {
            // Sanity: no permission before creating the assignment.
            (await Mesh.HasPermissionAsync(scope, userId, Permission.Read, TestTimeout))
                .Should().BeFalse();

            accessService.SetCircuitContext(new AccessContext { ObjectId = "globaladmin", Name = "globaladmin" });

            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var assignment = AssignmentNodeFactory.UserRole(userId, "Admin", scope);
            await meshService.CreateNode(assignment).FirstAsync().ToTask(TestTimeout);

            // After CreateNode completes, HasPermission must observe the new role.
            // Allow up to 5 s for the synced query to settle (matches the
            // production AccessControlPipeline timeout).
            var deadline = DateTime.UtcNow.AddSeconds(5);
            bool granted;
            do
            {
                granted = await Mesh.HasPermissionAsync(scope, userId, Permission.Read, TestTimeout);
                if (granted) break;
                await Task.Delay(100, TestTimeout);
            } while (DateTime.UtcNow < deadline);

            granted.Should().BeTrue(
                "Admin role granted at runtime via CreateNode must be visible to HasPermission " +
                "within 5 s — otherwise AccessControlPipeline rejects every message that " +
                "would have been authorized by that assignment.");
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task AnonymousUser_GetsAnonymousPermissions()
    {
        var permissions = await Mesh.GetPermissionAsync("org/public/area", "", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api);
    }

    /// <summary>
    /// Regression for the 2026-05-10 chat-render slowness in
    /// <c>Memex.Portal.Distributed</c>: every fresh permission check for
    /// a user without a static AccessAssignment paid the 2 s
    /// <c>GetUserScopeRolesStream</c> Timeout fallback before falling
    /// through to claim-based roles. Opening a chat thread fired
    /// hundreds of these per render, each logging
    /// <c>GetUserScopeRolesStream timed out for ...</c> on its way to the
    /// Empty-Catch fallback. Cumulative: tens of seconds of wasted wall
    /// time on a single click.
    ///
    /// <para>Health threshold: 20 unique-user permission checks must
    /// complete in well under the cumulative Timeout budget (20 × 2 s =
    /// 40 s if every check hit the fallback). 5 s catches the regression
    /// — one regressed check adds ~2 s, two push past the threshold.</para>
    ///
    /// <para>The target fix replaces the per-user MemoryCache + scope:subtree
    /// synced query with a per-scope cached 4-query union — see
    /// <see href="../Architecture/AccessControl.md">AccessControl.md</see>
    /// "Effective-assignments lookup: the 4-query union".</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task PermissionChecks_DoNotPayTimeoutFallback()
    {
        // Warm the SecurityService with one throw-away check so the
        // first-call cold paths (DI resolution, hub activation) don't
        // count against the perf budget — the bug we're catching is
        // per-check timeout, not first-call init.
        await Mesh.GetPermissionAsync("warmup/scope", "warmup-user", TestTimeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            await Mesh.GetPermissionAsync($"some/scope/{i}", $"u{i}", TestTimeout);
        }
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "permission checks must not pay the 2 s GetUserScopeRolesStream " +
            "timeout fallback. If this asserts > 5 s, the synced-query Initial " +
            "is not landing within the timeout window for cold users/scopes — " +
            "see AccessControl.md > 4-query union for the target shape.");
    }

    [Fact(Timeout = 20000)]
    public Task GetRole_ReturnsBuiltInRole()
    {
        // Built-in roles are static — assert directly. The (gone) SecurityService.GetRole
        // surface is no longer reachable from tests; the per-hub scoped service handles
        // role merging at permission-evaluation time.
        Role.Admin.Permissions.Should().Be(Permission.All);
        return Task.CompletedTask;
    }

    [Fact(Timeout = 20000)]
    public Task GetRoles_ReturnsAllBuiltInRoles()
    {
        // Built-in role registry is static.
        new[] { Role.Admin, Role.Editor, Role.Viewer, Role.Commenter }
            .Select(r => r.Id)
            .Should().Contain(["Admin", "Editor", "Viewer", "Commenter"]);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Tests for the RlsNodeValidator integration.
/// </summary>
public class RlsNodeValidatorTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => CancellationTokenSource.CreateLinkedTokenSource(
        new CancellationTokenSource(10.Seconds()).Token,
        TestContext.Current.CancellationToken).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("authorized-user", "Admin", "allowed/area"));
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

        var result = await validator!.Validate(context).FirstAsync().ToTask(TestTimeout);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.Unauthorized);
    }

    [Fact(Timeout = 20000)]
    public async Task ValidateAsync_WithPermission_ReturnsValid()
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

        var result = await validator!.Validate(context).FirstAsync().ToTask(TestTimeout);

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

            var result = await validator!.Validate(context).FirstAsync().ToTask(TestTimeout);
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

        var result = await validator!.Validate(context).FirstAsync().ToTask(TestTimeout);
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
    private CancellationToken TestTimeout => CancellationTokenSource.CreateLinkedTokenSource(
        new CancellationTokenSource(10.Seconds()).Token,
        TestContext.Current.CancellationToken).Token;

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

        await NodeFactory.CreateNode(
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
    public async Task Hub_CanQueryOwnNode_WithImpersonateAsHub()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

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
/// Tests for security using sample-Graph–like data, seeded via static AccessAssignment nodes.
/// </summary>
public class SampleDataSecurityTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // All [Fact]s do read-only permission checks (Mesh.HasPermissionAsync /
    // Mesh.GetPermissionAsync) against statically-seeded role assignments —
    // no test mutates the security state. Safe to share SP across tests.
    protected override bool ShareMeshAcrossTests => true;

    private CancellationToken TestTimeout => CancellationTokenSource.CreateLinkedTokenSource(
        new CancellationTokenSource(10.Seconds()).Token,
        TestContext.Current.CancellationToken).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole("Roland", "Admin", null),
                AssignmentNodeFactory.UserRole("Alice", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("Public", "Viewer", "MeshWeaver"));
    }

    [Fact(Timeout = 20000)]
    public async Task Roland_WithGlobalAdminRole_CanEditArchitectureNode()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        const string userId = "Roland";
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        var permissions = await Mesh.GetPermissionAsync(nodePath, userId, TestTimeout);
        var canEdit = await Mesh.HasPermissionAsync(nodePath, userId, Permission.Update, TestTimeout);

        permissions.Should().Be(Permission.All, "Roland has global Admin role");
        canEdit.Should().BeTrue("Roland should be able to edit the Architecture node");
    }

    [Fact(Timeout = 20000)]
    public async Task Roland_GlobalAdmin_CanEditAnyNode()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
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
            var canEdit = await Mesh.HasPermissionAsync(path, userId, Permission.Update, TestTimeout);
            canEdit.Should().BeTrue($"Roland should be able to edit '{path}' as global Admin");
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Alice_WithAcmeEditorRole_CanEditInAcmeOnly()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        const string userId = "Alice";

        var canEditAcme = await Mesh.HasPermissionAsync("ACME/Project/Task1", userId, Permission.Update, TestTimeout);
        var canEditMeshWeaver = await Mesh.HasPermissionAsync("MeshWeaver/Documentation", userId, Permission.Update, TestTimeout);

        canEditAcme.Should().BeTrue("Alice should be able to edit in Software namespace");
        canEditMeshWeaver.Should().BeFalse("Alice should NOT be able to edit in MeshWeaver namespace");
    }

    [Fact(Timeout = 20000)]
    public async Task PublicUser_WithMeshWeaverViewerRole_CannotEdit()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        const string userId = "Public";
        const string nodePath = "MeshWeaver/Documentation/Architecture";

        var canEdit = await Mesh.HasPermissionAsync(nodePath, userId, Permission.Update, TestTimeout);
        var canRead = await Mesh.HasPermissionAsync(nodePath, userId, Permission.Read, TestTimeout);

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
    private CancellationToken TestTimeout => CancellationTokenSource.CreateLinkedTokenSource(
        new CancellationTokenSource(10.Seconds()).Token,
        TestContext.Current.CancellationToken).Token;

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
    public async Task PolicyCapsPermissions_EditorCappedToRead()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync("org/docs", "editor1", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api | Permission.Export);
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyCapsAdmin_GlobalAdminCappedToRead()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        const string userId = "admin1";
        const string docNs = "platform/docs";

        var docPermissions = await Mesh.GetPermissionAsync(docNs, userId, TestTimeout);
        docPermissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api | Permission.Export);

        var childPermissions = await Mesh.GetPermissionAsync("platform/docs/readme", userId, TestTimeout);
        childPermissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api | Permission.Export);

        var otherPermissions = await Mesh.GetPermissionAsync("platform/code", userId, TestTimeout);
        otherPermissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyDoesNotAffectSiblingNamespace()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var acmePermissions = await Mesh.GetPermissionAsync("ACME/Project", "user2", TestTimeout);
        acmePermissions.Should().Be(Permission.All, "ACME should not be affected by Doc_test_pol policy");
    }

    [Fact(Timeout = 20000)]
    public async Task NestedPoliciesAccumulate()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var orgPermissions = await Mesh.GetPermissionAsync("org_nest/general", "user3", TestTimeout);
        orgPermissions.Should().Be(Permission.Read | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export, "org level allows Read + Comment + Execute + Thread + Api + Export");

        var restrictedPermissions = await Mesh.GetPermissionAsync("org_nest/restricted/item", "user3", TestTimeout);
        restrictedPermissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api | Permission.Export, "nested policy further restricts to Read + Execute + Api + Export only (Thread also denied)");
    }

    [Fact(Timeout = 20000)]
    public async Task BreaksInheritance_DiscardsParentRoles()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // user4 has Admin globally, but isolated namespace breaks inheritance
        var permissions = await Mesh.GetPermissionAsync("isolated/item", "user4", TestTimeout);
        permissions.Should().Be(Permission.None, "inherited Admin from global should be discarded");
    }

    [Fact(Timeout = 20000)]
    public async Task BreaksInheritance_KeepsLocalRoles()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // user5 has Admin globally + Editor at scoped (which breaks inheritance)
        var permissions = await Mesh.GetPermissionAsync("scoped/item", "user5", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export,
            "local Editor role should survive, inherited Admin should be discarded");
    }

    [Fact(Timeout = 20000)]
    public async Task PolicyAppliesToPublicUser()
    {
        var permissions = await Mesh.GetPermissionAsync("org/public_capped", WellKnownUsers.Public, TestTimeout);
        permissions.Should().Be(Permission.Execute | Permission.Api,
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
    // between fresh and reused SPs (probably an order-dependent registration
    // of the policy). Tests fail with Permission.All when SP is reused.

    private CancellationToken TestTimeout => CancellationTokenSource.CreateLinkedTokenSource(
        new CancellationTokenSource(10.Seconds()).Token,
        TestContext.Current.CancellationToken).Token;

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
    public async Task DocNamespace_AdminCappedToReadOnly()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("Doc/GettingStarted", "admin_doc", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export, "Doc namespace allows read + comment + thread + export but not create/update/delete");
    }

    [Fact(Timeout = 20000)]
    public async Task DocNamespace_EditorCappedToReadOnly()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("Doc/AI/AgenticAI", "editor_doc", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export, "Doc namespace allows read + comment + thread + export but not create/update/delete");
    }

    [Fact(Timeout = 20000)]
    public async Task AgentNamespace_AdminCappedToReadOnly()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("Agent/ThreadNamer", "admin_agent", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api | Permission.Export, "Agent namespace has a static read-only policy");
    }

    [Fact(Timeout = 20000)]
    public async Task RoleNamespace_AdminCappedToReadOnly()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("Role/Admin", "admin_role", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api | Permission.Export, "Role namespace has a static read-only policy");
    }

    [Fact(Timeout = 20000)]
    public async Task StaticPolicy_DoesNotAffectOtherNamespaces()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("ACME/Project/Task1", "admin_other", TestTimeout);
        permissions.Should().Be(Permission.All, "ACME is not a static namespace, Admin should have full access");
    }

    [Fact(Timeout = 20000)]
    public async Task StaticPolicy_DocRootItselfCapped()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var permissions = await Mesh.GetPermissionAsync("Doc", "admin_docroot", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export, "Doc root itself should be capped to Read + Comment + Execute + Thread + Api + Export");
    }
}
