using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public async Task BasicMessage()
    {
        var client = CreateClient();
        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(new MeshAddress())
                , new CancellationTokenSource(10.Seconds()).Token
                );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }


    [Theory]
    [InlineData("HubFactory")]
    //[InlineData("Kernel")]
    public async Task DisposeTest(string id)
    {
        var client = CreateClient();
        var address = new ApplicationAddress(id);

        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(address)
                , new CancellationTokenSource(10.Seconds()).Token
            );
        response.Should().NotBeNull();

        client.Post(new DisposeRequest(), o => o.WithTarget(address));
        await Task.Delay(100);
        response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(address)
 //               , new CancellationTokenSource(10.Seconds()).Token
            );
        response.Should().NotBeNull();
    }


    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureMesh(mesh => mesh
                .AddMeshNodes(new MeshNode(ApplicationAddress.TypeName, "HubFactory", "HubFactory", "host")
                {
                    HubConfiguration = x => x
                })
                .AddMeshNodes(new MeshNode(ApplicationAddress.TypeName, "Kernel", "Kernel", "host")
                {
                    StartupScript = @$"using MeshWeaver.Messaging; Mesh.ServiceProvider.CreateMessageHub(new {typeof(ApplicationAddress).FullName}(""Kernel""))"
                })
            ).AddKernel();
}
