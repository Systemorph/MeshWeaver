using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Messaging.Hub.Test;

public class MessageHubHelloWorldTest : TestBase
{
    record RouterAddress;
    record HostAddress;
    record ClientAddress;

    record SayHelloRequest : IRequest<HelloEvent>;
    record HelloEvent;

    // TODO V10: We need a setup in which router is resolved from top level DI, then host and client are resolved from the DI of the router hub. (18.01.2024, Roland Buergi)
    [Inject] private IMessageHub Router { get; set; }

    public MessageHubHelloWorldTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(new RouterAddress(), hubConf => hubConf
            .WithHostedHub<HostAddress>(host => host.WithHandler<SayHelloRequest>((hub, request) =>
            {
                hub.Post(new HelloEvent(), options => options.ResponseFor(request));
                return request.Processed();
            }))
            .WithHostedHub<ClientAddress>(client => client)
            // .WithMessageForwarding(f => f
            //     .RouteAddress<HostAddress>(delivery => f.Hub.ServiceProvider.GetRequiredService<IMessageHub<HostAddress>>().DeliverMessage(delivery))
            //     // .RouteAddress<ClientAddress>(delivery => f.Hub.ServiceProvider.GetRequiredService<IMessageHub<ClientAddress>>().DeliverMessage(delivery))
            // )
            ));

        Services.AddSingleton(sp =>
            sp
                .CreateMessageHub
                (
                    new HostAddress(),
                    hubConf => hubConf.WithHandler<SayHelloRequest>((hub, request) =>
                    {
                        hub.Post(new HelloEvent(), options => options.ResponseFor(request));
                        return request.Processed();
                    })));
        Services.AddSingleton(sp => sp.CreateMessageHub(new ClientAddress(), hubConf => hubConf));
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

    public override async Task DisposeAsync()
    {
        // TODO V10: This should dispose the other two. (18.01.2024, Roland Buergi)
        await Router.DisposeAsync();
    }
}