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
        var client = ServiceProvider.CreateMessageHub(
            new SignalRClientAddress(),
                config => config
            .UseSignalRClient(SignalRUrl)
            );

        var response = await client.AwaitResponse(new PingRequest(),
            o => o.WithTarget(new MeshAddress()),
            new CancellationTokenSource(3000.Seconds()).Token);
        response.Message.Should().BeOfType<PingResponse>();
    }
}
