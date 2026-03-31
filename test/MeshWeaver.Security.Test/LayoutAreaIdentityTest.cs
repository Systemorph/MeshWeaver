using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that layout area subscriptions carry the correct user identity
/// through SubscribeRequest.Identity, and that the AccessControlPipeline
/// resolves the user correctly for permission checks.
/// </summary>
public class LayoutAreaIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "IdentityTest/Target";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode("IdentityTest") { Name = "Identity Test" },
                new MeshNode("Target", "IdentityTest") { Name = "Target Node" }
            )
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    protected override async Task SetupAccessRightsAsync()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync(
            TestUsers.Admin.ObjectId, "Admin", "IdentityTest", "system",
            TestContext.Current.CancellationToken);
        await securityService.AddUserRoleAsync(
            "Viewer1", "Viewer", "IdentityTest", "system",
            TestContext.Current.CancellationToken);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private IMessageHub GetClientWithUser(string userId)
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });
        return client;
    }

    [Fact(Timeout = 10000)]
    public async Task AuthorizedUser_CanSubscribe_ToLayoutArea()
    {
        var client = GetClientWithUser("Viewer1");
        var nodeAddress = new Address(NodePath);

        await client.AwaitResponse(
            new PingRequest(), o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea));

        var result = await stream.Timeout(3.Seconds()).FirstAsync();
        result.Should().NotBeNull("Viewer1 has Read permission and should receive layout data");
    }

    [Fact(Timeout = 10000)]
    public async Task UnauthorizedUser_SubscriptionDenied()
    {
        var client = GetClientWithUser("NoAccess");
        var nodeAddress = new Address(NodePath);

        await client.AwaitResponse(
            new PingRequest(), o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea));

        Func<Task> act = async () => await stream.Timeout(3.Seconds()).FirstAsync();

        var ex = await Assert.ThrowsAsync<DeliveryFailureException>(act);
        ex.Message.Should().Contain("Access denied");
        ex.Message.Should().Contain("NoAccess");
    }

    [Fact(Timeout = 10000)]
    public async Task SubscribeRequest_CarriesIdentity()
    {
        // Verify that SubscribeRequest.Identity is stamped with the user ID
        var client = GetClientWithUser("Viewer1");
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // The AccessService should have CircuitContext set
        accessService.CircuitContext.Should().NotBeNull();
        accessService.CircuitContext!.ObjectId.Should().Be("Viewer1");

        // When the client creates a remote stream, the SubscribeRequest
        // should have Identity = "Viewer1" (stamped from CircuitContext)
        var nodeAddress = new Address(NodePath);
        await client.AwaitResponse(
            new PingRequest(), o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea));

        // If we get data (not access denied), the Identity was correctly resolved
        var result = await stream.Timeout(3.Seconds()).FirstAsync();
        result.Should().NotBeNull(
            "SubscribeRequest.Identity should carry 'Viewer1' which has Read permission");
    }

    [Fact(Timeout = 10000)]
    public async Task ViewerUser_CannotUpdate()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var perms = await securityService.GetEffectivePermissionsAsync(
            "IdentityTest/Target", "Viewer1", TestContext.Current.CancellationToken);

        perms.HasFlag(Permission.Read).Should().BeTrue("Viewer1 has Read");
        perms.HasFlag(Permission.Update).Should().BeFalse("Viewer1 lacks Update");
        perms.HasFlag(Permission.Create).Should().BeFalse("Viewer1 lacks Create");
    }
}
