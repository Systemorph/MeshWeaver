using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
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

        var client = GetClientWithUser("Viewer1");
        var nodeAddress = new Address(NodePath);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
            nodeAddress, reference);

        // With Read permission, should get data (no access denied)
        var result = await stream
            .Timeout(3.Seconds())
            .FirstAsync();

        // If we get here without DeliveryFailureException, the access check passed
        result.Should().NotBeNull("authorized user should receive stream data");
    }
}
