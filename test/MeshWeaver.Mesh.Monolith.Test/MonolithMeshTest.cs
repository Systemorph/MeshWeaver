using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Test;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithMeshTest : ConfiguredMeshTestBase
{
    public MonolithMeshTest(ITestOutputHelper output) : base(output)
    {
        ConfigureMesh(new MeshBuilder(config => config.Invoke(Services), new MeshAddress()));
    }


    [Fact]
    public async Task BasicMessage()
    {
        var client = ServiceProvider.CreateMessageHub(new ClientAddress(), conf => conf.WithTypes(typeof(Ping), typeof(Pong)));
        var mesh = ServiceProvider.GetRequiredService<IMessageHub>();
        var routingService = ServiceProvider.GetRequiredService<IRoutingService>();
        await routingService.RegisterHubAsync(client);
        var response = await client
            .AwaitResponse(new Ping(), o => o.WithTarget(TestApplicationAttribute.Address)
                //, new CancellationTokenSource(3.Seconds()).Token
                );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<Pong>();
    }

}
