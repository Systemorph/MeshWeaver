using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
    [Fact(Timeout = 20000)]
    public async Task PingPong()
    {
        var client = GetClient();
        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(Mesh.Address)
                , new CancellationTokenSource(10.Seconds()).Token
                );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }


    [Theory(Timeout = 20000)]
    [InlineData("HubFactory")]
    [InlineData("Kernel")]
    public async Task HubWorksAfterDisposal(string id)
    {
        var client = GetClient();
        var address = AddressExtensions.CreateAppAddress(id);

        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(address)
                , new CancellationTokenSource(10.Seconds()).Token
            );
        response.Should().NotBeNull();

        client.Post(new DisposeRequest(), o => o.WithTarget(address));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(address)
                , new CancellationTokenSource(10.Seconds()).Token
            );
        response.Should().NotBeNull();
    }


    [Fact(Timeout = 5000)]
    public async Task PingToNonExistentHub_ThrowsDeliveryFailure()
    {
        var client = GetClient();
        var nonExistentAddress = new Address("NonExistent", "Hub");

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nonExistentAddress),
                new CancellationTokenSource(3.Seconds()).Token);
        });

        // Should be a routing failure, NOT a timeout
        ex.Should().NotBeOfType<OperationCanceledException>();
        ex.Should().NotBeOfType<TaskCanceledException>();
        ex.GetBaseException().Message.Should().Contain("No node found");
    }

    [Fact(Timeout = 20000)]
    public async Task StreamToNonExistentHub_ThrowsDeliveryFailure()
    {
        var client = GetClient(c => c.AddData());
        var nonExistentAddress = new Address("NonExistent", "Hub");

        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nonExistentAddress,
            new LayoutAreaReference("Overview"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await stream.Timeout(5.Seconds()).FirstAsync();
        });

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
