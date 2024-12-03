using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Test;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class SignalRMeshTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{


    [Fact]
    public async Task PingPong()
    {
        var client = SignalRMeshClient
            .Configure(new ClientAddress(), SignalRUrl)
            .WithHttpConnectionOptions(options => options.HttpMessageHandlerFactory = (_ => Server.CreateHandler()))
            .ConfigureHub(config => config.WithTypes(typeof(Ping), typeof(Pong)))
            .Build();

        var response = await client.AwaitResponse(new Ping(),
            o => o.WithTarget(new ApplicationAddress(TestApplicationAttribute.Test)),
            new CancellationTokenSource(3000.Seconds()).Token);
        response.Message.Should().BeOfType<Pong>();
    }

}
