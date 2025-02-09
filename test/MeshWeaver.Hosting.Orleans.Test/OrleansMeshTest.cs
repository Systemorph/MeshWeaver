using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using Xunit.Abstractions;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Orleans.Test;

public class OrleansMeshTests(ITestOutputHelper output) : OrleansTestBase(output)
{
    [Fact]
    public async Task BasicMessage()
    {
        var client = await GetClientAsync();
        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(new ApplicationAddress(OrleansTestMeshNodeAttribute.OrleansTest.Name))
                //, new CancellationTokenSource(10.Seconds()).Token
            );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }
}

