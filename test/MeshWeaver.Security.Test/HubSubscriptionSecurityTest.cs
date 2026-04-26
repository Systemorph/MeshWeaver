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

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that RLS correctly denies read access for unauthorized users
/// and grants it for authorized users at the subscription level.
/// SubscribeRequest is marked with [RequiresPermission(Permission.Read)] and the
/// AccessControlPipeline (registered by AddRowLevelSecurity) checks permissions
/// before the message reaches the handler.
/// </summary>
public class HubSubscriptionSecurityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("SecuredHub") { Name = "Secured Hub" },
                // Pre-seed admin's Viewer access on SecuredHub for the
                // "Subscription_WithReadAccess_PassesAccessCheck" test — static
                // node provider seeds AccessAssignment at hub init time.
                AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Viewer", scope: "SecuredHub")
            )
            .ConfigureDefaultNodeHub(c => c.AddData(d => d));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddData(d => d);

    /// <summary>
    /// Skip PublicAdminAccess â€” security tests need granular permissions.
    /// </summary>
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// A user with no roles should have no read permission on the hub.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task UnauthorizedUser_HasNoReadPermission()
    {
        var permissions = await Mesh.GetPermissionAsync("SecuredHub", "NobodyUser", TestTimeout);

        permissions.Should().Be(Permission.None,
            "user with no role assignments should have zero permissions");
    }

    /// <summary>
    /// With read access granted, the subscription should pass the access check.
    /// The stream error is about unmapped collections, not about access denial.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Subscription_WithReadAccess_PassesAccessCheck()
    {
        // Admin's Viewer access on SecuredHub is pre-seeded via the static
        // AccessAssignment in ConfigureMesh — no runtime mutation needed.
        var hubAddress = new Address("SecuredHub");

        // Ensure hub is started
        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(hubAddress)).FirstAsync().ToTask();

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<EntityStore>(hubAddress, new CollectionsReference("test"));

        var act = async () => await stream
            .Timeout(5.Seconds())
            .FirstAsync();

        // With access, we don't get "Access denied" â€” the error is about unmapped collections
        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        ex.ToString().Should().NotContain("Access denied",
            "with read access, the error should NOT be about access denial");
    }
}
