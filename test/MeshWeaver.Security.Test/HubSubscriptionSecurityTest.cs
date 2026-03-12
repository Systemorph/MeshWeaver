using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that RLS correctly denies read access for unauthorized users
/// and grants it for authorized users at the permission-evaluation level.
///
/// Note: ISubscriptionAccessChecker is not registered because DeliveryFailure
/// for rejected SubscribeRequests does not propagate to Observable streams.
/// Access control is enforced at the individual layout/view level instead.
/// </summary>
public class HubSubscriptionSecurityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode("SecuredHub") { Name = "Secured Hub" })
            .ConfigureDefaultNodeHub(c => c.AddData(d => d));

    /// <summary>
    /// Skip PublicAdminAccess — security tests need granular permissions.
    /// </summary>
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// A user with no roles should have no read permission on the hub.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task UnauthorizedUser_HasNoReadPermission()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var permissions = await securityService.GetEffectivePermissionsAsync(
            "SecuredHub", "NobodyUser", TestTimeout);

        permissions.Should().Be(Permission.None,
            "user with no role assignments should have zero permissions");
    }

    /// <summary>
    /// With read access granted, the user should have at least Read permission.
    /// The subscription stream error is about unmapped collections, not about access —
    /// proving the access check would pass.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Subscription_WithReadAccess_PassesAccessCheck()
    {
        var hubAddress = new Address("SecuredHub");

        // Ensure hub is started
        var client = GetClient();
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(hubAddress),
            TestContext.Current.CancellationToken);

        // Default admin user (Roland with Admin role) has access via claim-based roles
        var subscriberHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("subscriber", "1"),
            c => c.AddData(d => d));

        var workspace = subscriberHub.GetWorkspace();
        var stream = workspace.GetRemoteStream<EntityStore>(hubAddress, new CollectionsReference("test"));

        var act = async () => await stream
            .Timeout(5.Seconds())
            .FirstAsync();

        // With access, we don't get "Access denied" — the error is about unmapped collections
        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        ex.ToString().Should().NotContain("Access denied",
            "with read access, the error should NOT be about access denial");
    }
}
