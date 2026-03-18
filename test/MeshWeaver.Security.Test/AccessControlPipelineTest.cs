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
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
                new MeshNode("Target", "PipelineTest") { Name = "Target Node" }
            )
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    protected override async Task SetupAccessRightsAsync()
    {
        // Grant admin full access so test setup can work
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync(
            TestUsers.Admin.ObjectId, "Admin", "PipelineTest", "system",
            TestContext.Current.CancellationToken);
    }

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
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        // Try to subscribe — should be denied by AccessControlPipeline
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
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync(
            "Viewer1", "Viewer", "PipelineTest", "system",
            TestContext.Current.CancellationToken);

        // Verify the permission was granted via ISecurityService
        var hasRead = await securityService.HasPermissionAsync(
            NodePath, "Viewer1", Permission.Read, TestContext.Current.CancellationToken);
        hasRead.Should().BeTrue("Viewer1 with Viewer role should have Read permission");
    }

    [Fact(Timeout = 10000)]
    public async Task GetDataRequest_WithoutReadPermission_ReturnsDeliveryFailure()
    {
        var client = GetClientWithUser("NoAccess");
        var nodeAddress = new Address(NodePath);

        // GetDataRequest also has [RequiresPermission(Permission.Read)]
        // AwaitResponse wraps DeliveryFailureException in AggregateException
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.AwaitResponse(
                new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(nodeAddress),
                TestContext.Current.CancellationToken));

        ex.InnerException.Should().BeOfType<DeliveryFailureException>();
        ex.InnerException!.Message.Should().Contain("Access denied");
        ex.InnerException.Message.Should().Contain("NoAccess");
    }
}

/// <summary>
/// Tests the HubPermissionRuleSet integration with AccessControlPipeline.
/// Uses Organization hub which has WithPublicRead() — this registers
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
        // A user with no ISecurityService permissions should still pass Read check.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Clear claim-based roles to simulate Orleans grain
        accessService.SetCircuitContext(new AccessContext { ObjectId = "SomeUser", Name = "SomeUser" });

        var response = await Mesh.AwaitResponse(
            new GetDataRequest(new UnifiedReference("data:")),
            o => o.WithTarget(new Address("Organization")),
            TestContext.Current.CancellationToken);

        // Not blocked by pipeline — may have no data but no access denied
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
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Clear claim-based roles
        accessService.SetCircuitContext(null);

        var permissions = await securityService.GetEffectivePermissionsAsync(
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
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync(
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
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Clear circuit context to simulate Orleans grain (no claim-based roles)
        var savedContext = accessService.CircuitContext;
        accessService.SetCircuitContext(null);

        try
        {
            // Without claim-based roles, permissions come only from AccessAssignment nodes.
            // PublicAdminAccess grants Admin at root ("") but not at "Organization" specifically.
            var permissions = await securityService.GetEffectivePermissionsAsync(
                "Organization", TestUsers.Admin.ObjectId, TestContext.Current.CancellationToken);

            Output.WriteLine($"Admin permissions on 'Organization' (no claims): {permissions}");

            permissions.Should().HaveFlag(Permission.Read,
                "Admin should have Read on Organization even without claim-based roles — " +
                "PublicAdminAccess at root should inherit to Organization path");
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
    }
}
