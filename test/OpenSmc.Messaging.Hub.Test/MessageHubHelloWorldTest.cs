using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using OpenSmc.Fixture;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Messaging.Hub.Test;

public class MessageHubHelloWorldTest : TestBase
{
    record RouterAddress; // TODO V10: can we use implicitly some internal address and not specify it outside? (23.01.2024, Alexander Yolokhov)
    record HostAddress;
    record ClientAddress;

    record SayHelloRequest : IRequest<HelloEvent>;
    record HelloEvent;

    [Inject] private IMessageHub Router { get; set; }

    public MessageHubHelloWorldTest(ITestOutputHelper output) : base(output)
    {
        Services.AddMessageHubs(new RouterAddress(), hubConf => hubConf
            .WithHostedHub<HostAddress>(host => host
                .WithHandler<SayHelloRequest>((hub, request) =>
                {
                    hub.Post(new HelloEvent(), options => options.ResponseFor(request));
                    return request.Processed();
                }))
            .WithHostedHub<ClientAddress>(client => client)
        );
    }

    [Fact]
    public async Task HelloWorld()
    {
        var host = Router.GetHostedHub(new HostAddress());
        var response = await host.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));
        response.Should().BeOfType<HelloEvent>();
    }

    [Fact]
    public async Task HelloWorldFromClient()
    {
        var client = Router.GetHostedHub(new ClientAddress());
        var response = await client.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));
        response.Should().BeOfType<HelloEvent>();
    }

    [Fact]
    public async Task Subscribers()
    {
        // arrange: initiate subscription from client to host
        var client = Router.GetHostedHub(new ClientAddress());
        await client.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new HostAddress()));
        var clientOut = client.Out.Timeout(500.Milliseconds());

        // act
        var host = Router.GetHostedHub(new HostAddress());
        host.Post(new HelloEvent(), o => o.WithTarget(MessageTargets.Subscribers));
        
        // assert
        var clientMessages = await clientOut.OfType<HelloEvent>().ToArray();
        clientMessages.Should().ContainSingle();
    }

    public override async Task DisposeAsync()
    {
        // TODO V10: This should dispose the other two. (18.01.2024, Roland Buergi)
        await Router.DisposeAsync();
    }
}