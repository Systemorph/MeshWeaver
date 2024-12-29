using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{

    [Fact]
    public async Task BasicMessage()
    {
        var client = CreateClient();
        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(new MeshAddress())
                //, new CancellationTokenSource(10.Seconds()).Token
                );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }

}
