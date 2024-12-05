using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Hosting.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class SignalRMeshTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{
    [Fact]
    public async Task PingPong()
    {
        var client = SignalRMeshClient
            .Configure(new SignalRClientAddress(), c => c.WithUrl(SignalRUrl, x => x.HttpMessageHandlerFactory = _ => Server.CreateHandler()))
            .ConfigureHub(config => config.WithTypes(typeof(Ping), typeof(Pong)))
            .Connect();

        var response = await client.AwaitResponse(new Ping(),
            o => o.WithTarget(new ApplicationAddress(TestApplicationAttribute.Test)),
            new CancellationTokenSource(3000.Seconds()).Token);
        response.Message.Should().BeOfType<Pong>();
    }
}
