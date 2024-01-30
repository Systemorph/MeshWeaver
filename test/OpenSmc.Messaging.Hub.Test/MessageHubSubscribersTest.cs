using FluentAssertions.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using OpenSmc.ServiceProvider;
using System.Reactive.Linq;
using FluentAssertions;
using OpenSmc.Hub.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Messaging.Hub.Test;

public class MessageHubSubscribersTest : TestBase
{
    protected record RouterAddress;

    protected record HostAddress;
    protected record ClientAddress(string Id);

    record SayHelloRequest : IRequest<HelloEvent>;
    record HelloEvent;

    [Inject] protected IMessageHub Router;

    public MessageHubSubscribersTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(new RouterAddress(),
            conf => conf
                .WithForwards(forward => forward
                    .RouteAddressToHostedHub<HostAddress>(ConfigureHost)
                    .RouteAddressToHostedHub<ClientAddress>(ConfigureClient)
                )));
    }
    protected IMessageHub GetHost()
    {
        return Router.GetHostedHub(new HostAddress(), ConfigureHost);
    }
    protected IMessageHub GetClient(string id)
    {
        return Router.GetHostedHub(new ClientAddress(id), ConfigureClient);
    }

    protected MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithHandler<SayHelloRequest>((hub, request) =>
            {
                hub.Post(new HelloEvent(), options => options.ResponseFor(request));
                return request.Processed();
            });

    protected MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) => configuration;

    [Fact]
    public async Task TwoSubscribers()
    {
        // arrange: initiate subscription from client to host
        var client1 = GetClient("1");
        var client2 = GetClient("2");
        var client3 = GetClient("3");

        var clientOut3 = client3.AddObservable(); // client 3 is not subscriber
        await client1.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));
        await client2.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));

        var clientOut1 = client1.AddObservable().Timeout(500.Milliseconds());
        var clientOut2 = client2.AddObservable().Timeout(500.Milliseconds());
        var client1Awaiter = clientOut1.Select(d => d.Message).OfType<HelloEvent>().FirstAsync().GetAwaiter();
        var client2Awaiter = clientOut2.Select(d => d.Message).OfType<HelloEvent>().FirstAsync().GetAwaiter();
        var client3Awaiter = clientOut3.Select(d => d.Message).OfType<HelloEvent>().ToArray().GetAwaiter();

        // act 
        var host = GetHost();
        host.Post(new HelloEvent(), o => o.WithTarget(MessageTargets.Subscribers));

        // assert
        var delay = Task.Delay(500.Microseconds());
        var client1Messages = await client1Awaiter;
        var client2Messages = await client2Awaiter;
        await delay;
        clientOut3.OnCompleted();
        var client3Messages = await client3Awaiter;

        client1Messages.Should().BeAssignableTo<HelloEvent>();
        client2Messages.Should().BeAssignableTo<HelloEvent>();
        client3Messages.Should().BeEmpty();
    }

}