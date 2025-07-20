using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Xunit;

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
            , new CancellationTokenSource(10.Seconds()).Token
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task HelloWorldFromClient()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(
            new SayHelloRequest(),
            o => o.WithTarget(new HostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task ClientToServerWithMessageTraffic()
    {
        var client = GetClient();

        var response = await client.AwaitResponse(
            new SayHelloRequest(),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

}
