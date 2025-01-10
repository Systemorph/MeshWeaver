using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Messaging.Hub.Test;

public class HostedHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record Ping : IRequest<Pong>;
    public record Pong;
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithTypes(typeof(Ping), typeof(Pong))
            .WithHandler<Ping>((hub,request) =>
            {
                hub.Post(new Pong(), o => o.ResponseFor(request));
                return request.Processed();
            });
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration);
    }

    public record NewAddress() : Address("new", "1");
    [Fact]
    public async Task HostedPingPong()
    {

        var client = GetClient();
        var subHub =
            client.ServiceProvider.CreateMessageHub(new NewAddress(),
                conf => conf.WithTypes(typeof(Ping), typeof(Pong))
                );
        var response = await subHub
            .AwaitResponse(new Ping(), o => o.WithTarget(new HostAddress())
                , new CancellationTokenSource(5.Seconds()).Token
                );
        response.Message.Should().BeOfType<Pong>();
    }

}
