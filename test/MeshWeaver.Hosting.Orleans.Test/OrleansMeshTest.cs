using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using Xunit.Abstractions;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Messaging;
using MeshWeaver.Connection.Orleans;
using Orleans;

namespace MeshWeaver.Hosting.Orleans.Test;

public class OrleansMeshTests(ITestOutputHelper output) : OrleansTestBase(output)
{
    [Fact]
    public async Task PingPong()
    {
        var client = await GetClientAsync();
        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(OrleansTestMeshNodeAttribute.Address)
                , new CancellationTokenSource(20.Seconds()).Token
            );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }
}

