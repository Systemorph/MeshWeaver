using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Hosting.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class SignalRMeshTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{


    [Fact]
    public async Task PingPong()
    {
        var client = MeshClient
            .Configure(SignalRUrl)
            .ConfigureHub(config => config.WithTypes(typeof(Ping), typeof(Pong)))
            .Connect();

        var response = await client.AwaitResponse(new Ping(),
            o => o.WithTarget(new ApplicationAddress(TestApplicationAttribute.Test)),
            new CancellationTokenSource(3000.Seconds()).Token);
        response.Message.Should().BeOfType<Pong>();
    }

}
