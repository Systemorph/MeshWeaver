using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 20000)]
    public void PingPong()
    {
        var client = GetClient();
        var response = client
            .Observe(new PingRequest(), o => o.WithTarget(Mesh.Address))
            .Should().Within(10.Seconds()).Emit();
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }


    [Fact(Timeout = 20000)]
    public void PingToNonExistentHub_ThrowsDeliveryFailure()
    {
        var client = GetClient();
        var nonExistentAddress = new Address("NonExistent", "Hub");

        var notification = client.Observe(new PingRequest(), o => o.WithTarget(nonExistentAddress))
            .Materialize()
            .Should().Within(10.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        var ex = notification.Exception!;

        // Should be a routing failure, NOT a timeout
        ex.Should().NotBeOfType<OperationCanceledException>();
        ex.Should().NotBeOfType<TaskCanceledException>();
        ex.GetBaseException().Message.Should().Contain("No node found");
    }

    [Fact(Timeout = 20000)]
    public void StreamToNonExistentHub_ThrowsDeliveryFailure()
    {
        var client = GetClient(c => c.AddData());
        var nonExistentAddress = new Address("NonExistent", "Hub");

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nonExistentAddress,
            new LayoutAreaReference("Overview"));

        var notification = stream
            .Materialize()
            .Should().Within(10.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        var ex = notification.Exception!;

        // Should be a DeliveryFailure, NOT a timeout
        ex.Should().NotBeOfType<TimeoutException>();
        ex.GetBaseException().Should().BeOfType<DeliveryFailureException>();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddMeshNodes(MeshNode.FromPath($"{AddressExtensions.AppType}/HubFactory") with
            {
                Name = "HubFactory",
                HubConfiguration = x => x
            })
                .AddMeshNodes(MeshNode.FromPath($"{AddressExtensions.AppType}/Kernel") with
                {
                    Name = "Kernel",
                    HubConfiguration = x => x
                })
; // AddKernel() is already included via AddGraph() in base class
}
