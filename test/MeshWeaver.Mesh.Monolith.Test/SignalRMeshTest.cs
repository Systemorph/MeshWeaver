using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class SignalRMeshTest(ITestOutputHelper output) : AspNetCoreMeshBase(output)
{
    [Fact]
    public async Task PingPong()
    {
        var services = CreateServiceCollection();
        var serviceProvider = services.CreateMeshWeaverServiceProvider();
        using var client = serviceProvider.CreateMessageHub(
            new SignalRAddress(),
                config => config
            .UseSignalRClient(SignalRUrl)
            );

        var response = await client.AwaitResponse(new PingRequest(),
            o => o.WithTarget(new MeshAddress())
            //, new CancellationTokenSource(10.Seconds()).Token
            );
        response.Message.Should().BeOfType<PingResponse>();
    }
}
