using FluentAssertions;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Mesh.Test;

public class MonolithMeshTest(ITestOutputHelper output) : ConfiguredMeshTestBase(output)
{

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
    {
        return base.ConfigureClient(config)
            .WithTypes(typeof(Ping), typeof(Pong));
    }

    [Fact]
    public async Task BasicMessage()
    {
        var client = await GetClient();
        var response = await client
            .AwaitResponse(new Ping(), o => o.WithTarget(TestApplicationAttribute.Address));
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<Pong>();
    }

}
