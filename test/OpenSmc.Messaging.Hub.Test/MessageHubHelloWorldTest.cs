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

    [Inject] private IMessageHub<HostAddress> Host;
    [Inject] private IMessageHub<ClientAddress> Client;

    public MessageHubHelloWorldTest(ITestOutputHelper output) : base(output)
    {
        // HACK: need to distinguish root hub by address type "object" to not have stack overflow when resolving IMessageHub in MessageHubConfiguration.Initialize (16.01.2024, Alexander Yolokhov)
        var routerAddress = (object)null;
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(routerAddress, hubConf => hubConf
            .WithMessageForwarding(f => f
                .RouteAddress<HostAddress>(delivery => sp.GetRequiredService<IMessageHub<HostAddress>>().DeliverMessage(delivery))
                .RouteAddress<ClientAddress>(delivery => sp.GetRequiredService<IMessageHub<ClientAddress>>().DeliverMessage(delivery))
            )));

        Services.AddSingleton(sp => sp.CreateMessageHub(new HostAddress(), hubConf => hubConf.WithHandler<SayHelloRequest>((hub, request) =>
        {
            hub.Post(new HelloEvent(), options => options.ResponseFor(request));
            return request.Processed();
        })));
        Services.AddSingleton(sp => sp.CreateMessageHub(new ClientAddress(), hubConf => hubConf));
    }

    [Fact]
    public async Task HelloWorld()
    {
        var response = await Host.AwaitResponse(new SayHelloRequest());
        response.Should().BeOfType<HelloEvent>();
    }

    [Fact]
    public async Task HelloWorldFromClient()
    {
        var response = await Client.AwaitResponse(new SayHelloRequest());
        response.Should().BeOfType<HelloEvent>();
    }

    public override Task DisposeAsync()
    {
        return base.DisposeAsync();
    }
}