using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Messaging.Hub.Test;

public class MessageHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    record SayHelloRequest : IRequest<HelloEvent>;

    record HelloEvent;

    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) =>
        configuration.WithHandler<SayHelloRequest>(
            (hub, request) =>
            {
                hub.Post(new HelloEvent(), options => options.ResponseFor(request));
                return request.Processed();
            }
        );

    [Fact]
    public async Task HelloWorld()
    {
        var host = GetHost();
        var response = await host.AwaitResponse(
            new SayHelloRequest(),
            o => o.WithTarget(new HostAddress())
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task HelloWorldFromClient()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(
            new SayHelloRequest(),
            o => o.WithTarget(new HostAddress())
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task ClientToServerWithMessageTraffic()
    {
        var client = GetClient();

        var response = await client.AwaitResponse(
            new SayHelloRequest(),
            o => o.WithTarget(new HostAddress())
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

}
