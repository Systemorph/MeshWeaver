using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests the AccessControlPipeline delivery pipeline step.
/// Verifies that messages with [RequiresPermission] are blocked when the user
/// lacks the required permission, and that the DeliveryFailure propagates
/// correctly to stream consumers.
/// </summary>
public class AccessControlPipelineTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "PipelineTest/Target";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode("PipelineTest") { Name = "Pipeline Test" },
                new MeshNode("Target", "PipelineTest") { Name = "Target Node" },
                // Admin's full access on PipelineTest scope (replaces SetupAccessRightsAsync mutation).
                AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", scope: "PipelineTest")
            )
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient();

    private IMessageHub GetClientWithUser(string userId)
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });
        return client;
    }

    [Fact(Timeout = 10000)]
    public void SubscribeRequest_WithoutReadPermission_ReturnsDeliveryFailure()
    {
        // User "NoAccess" has no roles at all
        var client = GetClientWithUser("NoAccess");
        var nodeAddress = new Address(NodePath);

        // Ensure hub is started
        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        // Try to subscribe â€” should be denied by AccessControlPipeline
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
            nodeAddress, reference);

        Action act = () => stream
            .Timeout(3.Seconds())
            .Wait();

        var ex = act.Should().Throw<DeliveryFailureException>().Which;
        ex.Message.Should().Contain("Access denied");
        ex.Message.Should().Contain("NoAccess");
        ex.Message.Should().Contain("Read");
    }

    [Fact(Timeout = 10000)]
    public void SubscribeRequest_WithReadPermission_Succeeds()
    {
        // Grant Viewer role at runtime (tests live behavior change after grant).
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(AssignmentNodeFactory.UserRole("Viewer1", "Viewer", "PipelineTest"))
            .Should().Emit();

        // Verify the permission was granted via the live effective-permission
        // stream. Probe for the Read flag and complete as soon as it surfaces.
        Mesh.GetEffectivePermissions(NodePath, "Viewer1")
            .Should().Match(p => p.HasFlag(Permission.Read),
                "Viewer1 with Viewer role should have Read permission");
    }

    [Fact(Timeout = 10000)]
    public void GetDataRequest_WithoutReadPermission_ReturnsDeliveryFailure()
    {
        var client = GetClientWithUser("NoAccess");
        var nodeAddress = new Address(NodePath);

        // GetDataRequest also has [RequiresPermission(Permission.Read)] —
        // the cold Observe observable surfaces DeliveryFailureException via OnError.
        Action act = () => client.Observe(new GetDataRequest(new UnifiedReference("data:")), o => o.WithTarget(nodeAddress)).Wait();

        var ex = act.Should().Throw<DeliveryFailureException>().Which;
        ex.Message.Should().Contain("Access denied");
        ex.Message.Should().Contain("NoAccess");
    }
}

/// <summary>
/// Tests the HubPermissionRuleSet integration with AccessControlPipeline.
/// Uses Organization hub which has WithPublicRead() â€” this registers
/// a HubPermissionRuleSet that grants Read to all authenticated users.
/// </summary>
public class HubPermissionRuleSetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSpaceType()
            .AddSampleUsers();

    [Fact(Timeout = 10000)]
    public void HubPermissionRuleSet_OnlyGrantsConfiguredPermission()
    {
        // WithPublicRead only grants Read, not Create/Update/Delete.
        // Verify at the rule set level.
        var ruleSet = new HubPermissionRuleSet()
            .Add(Permission.Read, (_, userId) => !string.IsNullOrEmpty(userId));

        ruleSet.HasPermission(Permission.Read, null!, "SomeUser")
            .Should().BeTrue("Read should be granted for authenticated user");

        ruleSet.HasPermission(Permission.Create, null!, "SomeUser")
            .Should().BeFalse("Create should NOT be granted by WithPublicRead");

        ruleSet.HasPermission(Permission.Update, null!, "SomeUser")
            .Should().BeFalse("Update should NOT be granted by WithPublicRead");

        ruleSet.HasPermission(Permission.Delete, null!, "SomeUser")
            .Should().BeFalse("Delete should NOT be granted by WithPublicRead");

        ruleSet.HasPermission(Permission.Read, null!, null)
            .Should().BeFalse("Read should NOT be granted for anonymous (null userId)");

        ruleSet.HasPermission(Permission.Read, null!, "")
            .Should().BeFalse("Read should NOT be granted for empty userId");
    }

    [Fact(Timeout = 10000)]
    public void Admin_CanReadOrganizationHub_WithoutClaimBasedRoles()
    {
        // In Orleans, claim-based roles from AccessContext aren't available.
        // Admin gets permissions from PublicAdminAccess at root, which should
        // inherit to Organization path. Additionally, Organization hub has
        // WithPublicRead → HubPermissionRuleSet.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Clear claim-based roles
        accessService.SetCircuitContext(null);

        var permissions = Mesh.GetEffectivePermissions("Space", TestUsers.Admin.ObjectId)
            .Should().Match(p => p.HasFlag(Permission.Read));

        Output.WriteLine($"Permissions without claims: {permissions}");

        permissions.Should().HaveFlag(Permission.Read,
            "Admin should have Read via PublicAdminAccess even without claim-based roles");
    }
}

/// <summary>
/// Tests that Organization NodeType hubs are accessible to admins.
/// Reproduces production issue: "Access denied: user 'rbuergi' lacks Read permission on 'Organization'"
/// In production (Orleans), claim-based roles from AccessContext aren't available on remote grains,
/// so the user needs explicit access assignments or PublicAdminAccess to have permissions.
/// </summary>
public class OrganizationHubAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSpaceType()
            .AddSampleUsers();

    [Fact(Timeout = 10000)]
    public void Admin_HasReadPermissionOnOrganizationPath()
    {
        var permissions = Mesh.GetEffectivePermissions("Space", TestUsers.Admin.ObjectId)
            .Should().Match(p => p.HasFlag(Permission.Read));

        Output.WriteLine($"Admin permissions on 'Space': {permissions}");

        permissions.Should().HaveFlag(Permission.Read,
            "Admin should have Read permission on Space NodeType path");
    }

}

/// <summary>
/// Tests that the User hub grants read access to authenticated users via HubPermissionRuleSet.
/// Reproduces deployed test environment error:
/// "Access denied: user 'sglauser@systemorph.com' lacks Read permission on 'User'"
/// The fix: WithUserNodePublicRead() must register AddHubPermissionRule (not just AddAccessRule).
/// </summary>
public class UserHubAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSampleUsers();

    [Fact(Timeout = 10000)]
    public void AuthenticatedUser_CanReadUserHub()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "unprivileged@example.com", Name = "Unprivileged User" });

        var response = Mesh
            .Observe(new GetDataRequest(new UnifiedReference("data:")), o => o.WithTarget(new Address("User")))
            .Should().Emit();

        response.Message.Error.Should().NotContain("Access denied",
            "authenticated user should have Read access to User hub via HubPermissionRuleSet");
    }

    [Fact(Timeout = 10000)]
    public void AnonymousUser_CannotReadUserHub()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "", Name = "" });

        Action act = () =>
            Mesh.Observe(new GetDataRequest(new UnifiedReference("data:")), o => o.WithTarget(new Address("User")))
                .Wait();

        var ex = act.Should().Throw<DeliveryFailureException>().Which;
        ex.Message.Should().Contain("Access denied");
    }
}

/// <summary>
/// Tests the self-scope fallback in SecurityService: a user always has Admin
/// permissions on their own User/{userId} scope and its children, without
/// needing explicit AccessAssignment nodes.
/// </summary>
public class UserSelfScopeAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // charlie's explicit Admin assignment — used by UserWithExplicitAdmin_FallbackIsNoOp
            // to verify the self-scope fallback is a no-op when an explicit grant already exists.
            // Post-v10: user partition lives at the root namespace (path={userId}).
            .AddMeshNodes(AssignmentNodeFactory.UserRole("charlie", "Admin", scope: "charlie"));

    [Fact(Timeout = 10000)]
    public void UserAccessingOwnScope_ReturnsAdmin()
    {
        var permissions = Mesh.GetEffectivePermissions("alice", "alice")
            .Should().Match(p => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Create)
                && p.HasFlag(Permission.Update) && p.HasFlag(Permission.Delete));

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().HaveFlag(Permission.Delete);
    }

    [Fact(Timeout = 10000)]
    public void UserAccessingOwnChild_ReturnsAdmin()
    {
        var permissions = Mesh.GetEffectivePermissions("bob/_Thread/t1", "bob")
            .Should().Match(p => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Create)
                && p.HasFlag(Permission.Update) && p.HasFlag(Permission.Delete));

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().HaveFlag(Permission.Delete);
    }

    [Fact(Timeout = 10000)]
    public void AnonymousAccessingUserScope_NoFallback()
    {
        Mesh.GetEffectivePermissions("alice", WellKnownUsers.Anonymous)
            .Should().Match(p => p == Permission.None,
                "Anonymous should not get self-scope fallback on another user's scope");
    }

    [Fact(Timeout = 10000)]
    public void PublicAccessingUserScope_NoFallback()
    {
        Mesh.GetEffectivePermissions("alice", WellKnownUsers.Public)
            .Should().Match(p => p == Permission.None,
                "Public should not get self-scope fallback on another user's scope");
    }

    [Fact(Timeout = 10000)]
    public void UserAccessingOtherUserScope_NoFallback()
    {
        Mesh.GetEffectivePermissions("alice", "bob")
            .Should().Match(p => p == Permission.None,
                "bob should not have any permissions on alice's scope without explicit assignment");
    }

    [Fact(Timeout = 10000)]
    public void UserWithExplicitAdmin_FallbackIsNoOp()
    {
        // charlie's explicit Admin is seeded statically in ConfigureMesh —
        // the assertion proves the self-scope fallback doesn't conflict with it.
        var permissions = Mesh.GetEffectivePermissions("charlie", "charlie")
            .Should().Match(p => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Create)
                && p.HasFlag(Permission.Update) && p.HasFlag(Permission.Delete));

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().HaveFlag(Permission.Delete);
    }

    [Fact(Timeout = 10000)]
    public void CaseInsensitivePath_Works()
    {
        // Path uses uppercase "Alice" — the userId match must be case-insensitive
        // so a user accessing their own scope under a casing-different prefix still
        // resolves through the self-scope fallback.
        var permissions = Mesh.GetEffectivePermissions("Alice/child", "alice")
            .Should().Match(p => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Create));

        permissions.Should().HaveFlag(Permission.Read,
            "self-scope fallback should be case-insensitive on path prefix");
        permissions.Should().HaveFlag(Permission.Create);
    }
}
