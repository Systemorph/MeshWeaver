using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithMeshTest(ITestOutputHelper output) : ConfiguredMeshTestBase(output)
{

    [Fact]
    public async Task BasicMessage()
    {
        var client = Mesh.ServiceProvider.CreateMessageHub(new ClientAddress(), conf => conf.WithTypes(typeof(PingRequest), typeof(PingResponse)));
        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(new MeshAddress())
                //, new CancellationTokenSource(3.Seconds()).Token
                );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }

}
