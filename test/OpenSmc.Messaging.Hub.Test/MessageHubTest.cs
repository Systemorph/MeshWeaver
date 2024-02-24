using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using OpenSmc.Hub.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Messaging.Hub.Test;
public class MessageHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    record SayHelloRequest : IRequest<HelloEvent>;
    record HelloEvent;


    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithHandler<SayHelloRequest>((hub, request) =>
            {
                hub.Post(new HelloEvent(), options => options.ResponseFor(request));
                return request.Processed();
            });


    [Fact]
    public async Task HelloWorld()
    {
        var host = GetHost();
        var response = await host.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }


    [Fact]
    public async Task HelloWorldFromClient()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task ClientToServerWithMessageTraffic()
    {
        var client = GetClient();

        var response = await client.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task Subscribers()
    {
        // arrange: initiate subscription from client to host
        var client = GetClient();
        await client.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));
        var clientOut = client.AddObservable().Timeout(500.Milliseconds());
        var clientMessagesTask = clientOut.Select(d => d.Message).OfType<HelloEvent>().FirstAsync().GetAwaiter();

        // act 
        var host = GetHost();
        host.Post(new HelloEvent(), o => o.WithTarget(MessageTargets.Subscribers));
        
        // assert
        var clientMessages = await clientMessagesTask;
        clientMessages.Should().BeAssignableTo<HelloEvent>();
    }

}
