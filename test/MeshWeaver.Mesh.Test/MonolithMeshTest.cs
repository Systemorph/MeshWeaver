using FluentAssertions;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Mesh.Test;

public class MonolithMeshTest(ITestOutputHelper output) : ConfiguredMeshTestBase(output)
{


    [Fact]
    public async Task BasicMessage()
    {
        var client = ServiceProvider.CreateMessageHub(new ClientAddress(), conf => conf.WithTypes(typeof(Ping), typeof(Pong)));
        var mesh = CreateMesh(ServiceProvider);
        var routingService = mesh.ServiceProvider.GetRequiredService<IRoutingService>();
        await routingService.RegisterHubAsync(client);
        var response = await client
            .AwaitResponse(new Ping(), o => o.WithTarget(TestApplicationAttribute.Address));
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<Pong>();
    }

}
