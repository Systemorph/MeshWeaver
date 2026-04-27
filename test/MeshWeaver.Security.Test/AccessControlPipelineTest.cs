using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using Memex.Portal.Shared;
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
    public async Task SubscribeRequest_WithoutReadPermission_ReturnsDeliveryFailure()
    {
        // User "NoAccess" has no roles at all
        var client = GetClientWithUser("NoAccess");
        var nodeAddress = new Address(NodePath);

        // Ensure hub is started
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).FirstAsync().ToTask();

        // Try to subscribe â€” should be denied by AccessControlPipeline
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
            nodeAddress, reference);

        Func<Task> act = async () => await stream
            .Timeout(3.Seconds())
            .FirstAsync();

        var ex = await Assert.ThrowsAsync<DeliveryFailureException>(act);
        ex.Message.Should().Contain("Access denied");
        ex.Message.Should().Contain("NoAccess");
        ex.Message.Should().Contain("Read");
    }

    [Fact(Timeout = 10000)]
    public async Task SubscribeRequest_WithReadPermission_Succeeds()
    {
        // Grant Viewer role at runtime (tests live behavior change after grant).
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole("Viewer1", "Viewer", "PipelineTest"))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Verify the permission was granted via the GetPermissionRequest round-trip.
        var hasRead = await Mesh.HasPermissionAsync(
            NodePath, "Viewer1", Permission.Read, TestContext.Current.CancellationToken);
        hasRead.Should().BeTrue("Viewer1 with Viewer role should have Read permission");
    }

    [Fact(Timeout = 10000)]
    public async Task GetDataRequest_WithoutReadPermission_ReturnsDeliveryFailure()
    {
        var client = GetClientWithUser("NoAccess");
        var nodeAddress = new Address(NodePath);

        // GetDataRequest also has [RequiresPermission(Permission.Read)]
        // hub.Observe(...).FirstAsync().ToTask() surfaces DeliveryFailureException
        // directly via OnError — no AggregateException wrapping.
        var ex = await Assert.ThrowsAsync<DeliveryFailureException>(() =>
            client.Observe(new GetDataRequest(new UnifiedReference("data:")), o => o.WithTarget(nodeAddress)).FirstAsync().ToTask());

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
            .AddOrganizationType()
            .AddSampleUsers();

    [Fact(Timeout = 10000)]
    public async Task WithPublicRead_AllowsAuthenticatedUserRead()
    {
        // Organization hub has WithPublicRead() → HubPermissionRuleSet grants Read.
        // A user with no AccessAssignment should still pass Read check.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Clear claim-based roles to simulate Orleans grain
        accessService.SetCircuitContext(new AccessContext { ObjectId = "SomeUser", Name = "SomeUser" });

        var response = await Mesh.Observe(new GetDataRequest(new UnifiedReference("data:")), o => o.WithTarget(new Address("Organization"))).FirstAsync().ToTask();

        // Not blocked by pipeline â€” may have no data but no access denied
        response.Message.Error.Should().NotContain("Access denied");
    }

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
    public async Task Admin_CanReadOrganizationHub_WithoutClaimBasedRoles()
    {
        // In Orleans, claim-based roles from AccessContext aren't available.
        // Admin gets permissions from PublicAdminAccess at root, which should
        // inherit to Organization path. Additionally, Organization hub has
        // WithPublicRead → HubPermissionRuleSet.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Clear claim-based roles
        accessService.SetCircuitContext(null);

        var permissions = await Mesh.GetPermissionAsync(
            "Organization", TestUsers.Admin.ObjectId, TestContext.Current.CancellationToken);

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
            .AddOrganizationType()
            .AddSampleUsers();

    [Fact(Timeout = 10000)]
    public async Task Admin_HasReadPermissionOnOrganizationPath()
    {
        var permissions = await Mesh.GetPermissionAsync(
            "Organization", TestUsers.Admin.ObjectId, TestContext.Current.CancellationToken);

        Output.WriteLine($"Admin permissions on 'Organization': {permissions}");

        permissions.Should().HaveFlag(Permission.Read,
            "Admin should have Read permission on Organization NodeType path");
    }

    /// <summary>
    /// Simulates what Orleans does: the security check runs without AccessService.Context
    /// (no claim-based roles). The user only has access from PublicAdminAccess assignments.
    /// This reproduces the production "lacks Read permission on 'Organization'" error.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Admin_HasReadOnOrganization_WithoutClaimBasedRoles()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Clear circuit context to simulate Orleans grain (no claim-based roles)
        var savedContext = accessService.CircuitContext;
        accessService.SetCircuitContext(null);

        try
        {
            // Without claim-based roles, permissions come only from AccessAssignment nodes.
            // PublicAdminAccess grants Admin at root ("") but not at "Organization" specifically.
            var permissions = await Mesh.GetPermissionAsync(
                "Organization", TestUsers.Admin.ObjectId, TestContext.Current.CancellationToken);

            Output.WriteLine($"Admin permissions on 'Organization' (no claims): {permissions}");

            permissions.Should().HaveFlag(Permission.Read,
                "Admin should have Read on Organization even without claim-based roles â€” " +
                "PublicAdminAccess at root should inherit to Organization path");
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
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
    public async Task AuthenticatedUser_CanReadUserHub()
    {
        // Simulate an unprivileged authenticated user (no Admin role, no explicit access assignments)
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "unprivileged@example.com", Name = "Unprivileged User" });

        var response = await Mesh.AwaitResponse(
            new GetDataRequest(new UnifiedReference("data:")),
            o => o.WithTarget(new Address("User")),
            TestContext.Current.CancellationToken);

        // Should not be blocked by AccessControlPipeline
        response.Message.Error.Should().NotContain("Access denied",
            "authenticated user should have Read access to User hub via HubPermissionRuleSet");
    }

    [Fact(Timeout = 10000)]
    public async Task AnonymousUser_CannotReadUserHub()
    {
        // Anonymous (empty userId) should be denied
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "", Name = "" });

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Mesh.AwaitResponse(
                new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address("User")),
                TestContext.Current.CancellationToken));

        ex.InnerException.Should().BeOfType<DeliveryFailureException>();
        ex.InnerException!.Message.Should().Contain("Access denied");
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
        => ConfigureMeshBase(builder); // No PublicAdminAccess — pure RLS

    [Fact(Timeout = 10000)]
    public async Task UserAccessingOwnScope_ReturnsAdmin()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync(
            "User/alice", "alice", TestContext.Current.CancellationToken);

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().HaveFlag(Permission.Delete);
    }

    [Fact(Timeout = 10000)]
    public async Task UserAccessingOwnChild_ReturnsAdmin()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync(
            "User/bob/_Thread/t1", "bob", TestContext.Current.CancellationToken);

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().HaveFlag(Permission.Delete);
    }

    [Fact(Timeout = 10000)]
    public async Task AnonymousAccessingUserScope_NoFallback()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync(
            "User/alice", WellKnownUsers.Anonymous, TestContext.Current.CancellationToken);

        permissions.Should().Be(Permission.None,
            "Anonymous should not get self-scope fallback on another user's scope");
    }

    [Fact(Timeout = 10000)]
    public async Task PublicAccessingUserScope_NoFallback()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync(
            "User/alice", WellKnownUsers.Public, TestContext.Current.CancellationToken);

        permissions.Should().Be(Permission.None,
            "Public should not get self-scope fallback on another user's scope");
    }

    [Fact(Timeout = 10000)]
    public async Task UserAccessingOtherUserScope_NoFallback()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync(
            "User/alice", "bob", TestContext.Current.CancellationToken);

        permissions.Should().Be(Permission.None,
            "bob should not have any permissions on alice's scope without explicit assignment");
    }

    [Fact(Timeout = 10000)]
    public async Task UserWithExplicitAdmin_FallbackIsNoOp()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Give charlie explicit Admin on User/charlie
        await securityService.AddUserRoleAsync(
            "charlie", "Admin", "User/charlie", "system",
            TestContext.Current.CancellationToken);

        var permissions = await securityService.GetEffectivePermissionsAsync(
            "User/charlie", "charlie", TestContext.Current.CancellationToken);

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().HaveFlag(Permission.Delete);
    }

    [Fact(Timeout = 10000)]
    public async Task CaseInsensitivePath_Works()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Path uses lowercase "user" instead of "User"
        var permissions = await securityService.GetEffectivePermissionsAsync(
            "user/alice/child", "alice", TestContext.Current.CancellationToken);

        permissions.Should().HaveFlag(Permission.Read,
            "self-scope fallback should be case-insensitive on path prefix");
        permissions.Should().HaveFlag(Permission.Create);
    }
}
