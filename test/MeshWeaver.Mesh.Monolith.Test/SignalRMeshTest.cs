using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Test;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class SignalRMeshTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{


    [Fact]
    public async Task PingPong()
    {
        var client = new SignalRMeshClient(new ClientAddress(), SignalRUrl)
            .WithHttpConnectionOptions(options => options.HttpMessageHandlerFactory = (_ => Server.CreateHandler()))
            .Build();

        var response = await client.AwaitResponse(new Ping(),
            o => o.WithTarget(new ApplicationAddress(TestApplicationAttribute.Test)),
            new CancellationTokenSource(3000.Seconds()).Token);
        response.Message.Should().BeOfType<Pong>();
    }

}
