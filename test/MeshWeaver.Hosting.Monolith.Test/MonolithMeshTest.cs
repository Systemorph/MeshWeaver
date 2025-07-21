using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
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


    [Theory]
    [InlineData("HubFactory")]
    [InlineData("Kernel")]
    public async Task HubWorksAfterDisposal(string id)
    {
        var client = GetClient();
        var address = new ApplicationAddress(id);

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


    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureMesh(mesh => mesh
                .AddMeshNodes(new MeshNode(ApplicationAddress.TypeName, "HubFactory", "HubFactory")
                {
                    HubConfiguration = x => x
                })
                .AddMeshNodes(new MeshNode(ApplicationAddress.TypeName, "Kernel", "Kernel")
                {
                    StartupScript = @$"using MeshWeaver.Messaging; Mesh.ServiceProvider.CreateMessageHub(new {typeof(ApplicationAddress).FullName}(""Kernel""))"
                })
            ).AddKernel();
}
