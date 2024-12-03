using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Application;
using MeshWeaver.Mesh.SignalR.Client;
using MeshWeaver.Mesh.Test;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Mesh.Monolith.Test;

public class SignalRMeshTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{

    protected MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
    {
        return config
            .UseSignalRMesh("http://localhost/connection",
                options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
            })
            .WithTypes(typeof(Ping), typeof(Pong));
    }



    [Fact]
    public async Task PingPong()
    {
        var address = new ClientAddress();
        var client = ServiceProvider.CreateMessageHub(address, config => config
            .UseSignalRMesh("http://localhost/connection",
                options =>
            {
                options.HttpMessageHandlerFactory = (_ => Server.CreateHandler());
            })
            .WithTypes(typeof(Ping), typeof(Pong)));

        var response = await client.AwaitResponse(new Ping(),
            o => o.WithTarget(new ApplicationAddress(TestApplicationAttribute.Test)),
            new CancellationTokenSource(3000.Seconds()).Token);
        response.Message.Should().BeOfType<Pong>();
    }


}
