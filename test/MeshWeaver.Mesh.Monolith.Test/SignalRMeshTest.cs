using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.SignalR;
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
        var client = await SignalRMeshClient
            .Configure(SignalRUrl)
            .ConfigureConnection(c => c.WithUrl(SignalRUrl, x => x.HttpMessageHandlerFactory = _ => Server.CreateHandler()))
            .ConnectAsync();

        var response = await client.AwaitResponse(new PingRequest(),
            o => o.WithTarget(new MeshAddress()),
            new CancellationTokenSource(3000.Seconds()).Token);
        response.Message.Should().BeOfType<PingResponse>();
    }
}
