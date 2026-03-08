using System;
using System.Reactive.Linq;
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

namespace MeshWeaver.Hosting.Monolith.Test.Security;

/// <summary>
/// Tests that hub subscriptions throw when RLS denies read access
/// instead of hanging/timing out silently.
/// </summary>
public class HubSubscriptionSecurityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode("SecuredHub") { Name = "Secured Hub" })
            .ConfigureDefaultNodeHub(c => c.AddData(d => d));
    // No PublicAdminAccess → no read access for anyone

    private async Task EnsureHubStarted(Address hubAddress)
    {
        var client = GetClient();
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(hubAddress),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Subscribing to a hub without read access should throw with "Access denied", not timeout.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Subscription_WithoutReadAccess_ShouldThrow()
    {
        var hubAddress = new Address("SecuredHub");
        await EnsureHubStarted(hubAddress);

        // Login as a user with NO roles — no permissions at all
        var noRoleUser = new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" };
        TestUsers.DevLogin(Mesh, noRoleUser);

        var subscriberHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("subscriber", "1"),
            c => c.AddData(d => d));

        var workspace = subscriberHub.GetWorkspace();
        var stream = workspace.GetRemoteStream<EntityStore>(hubAddress, new CollectionsReference("test"));

        var act = async () => await stream
            .Timeout(5.Seconds())
            .FirstAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        ex.Should().NotBeOfType<TimeoutException>(
            "subscription rejection should propagate as an exception, not cause a silent timeout");
        ex.ToString().Should().Contain("Access denied",
            "the error should indicate access was denied");
    }

    /// <summary>
    /// With read access, subscription reaches the handler (no access denied).
    /// The stream error is about unmapped collections, not about access — proving access check passed.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Subscription_WithReadAccess_PassesAccessCheck()
    {
        var hubAddress = new Address("SecuredHub");
        await EnsureHubStarted(hubAddress);

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
