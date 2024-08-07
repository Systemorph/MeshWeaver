using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Messaging.Hub.Test;

public class MessageHubHostedAddressTest : TestBase
{
    protected record RouterAddress;

    protected record HostAddress;
    protected record SubHubAddress(object Host) : IHostedAddress;
    protected record ClientAddress;

    record SayHelloRequest : IRequest<HelloEvent>;
    record HelloEvent;

    [Inject] protected IMessageHub Router;

    public MessageHubHostedAddressTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(new RouterAddress(),
            conf => conf
                .WithRoutes(forward => forward
                    .RouteAddressToHostedHub<HostAddress>(ConfigureHost)
                    .RouteAddressToHostedHub<ClientAddress>(ConfigureClient)
                )));
    }

    protected IMessageHub GetClient() => Router.GetHostedHub(new ClientAddress(), ConfigureClient);

    protected MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
                .WithRoutes(forward => forward
                    .RouteAddressToHostedHub<SubHubAddress>(ConfigureSubHub)
                );

    protected MessageHubConfiguration ConfigureSubHub(MessageHubConfiguration configuration)
        => configuration
            .WithHandler<SayHelloRequest>((hub, request) =>
            {
                hub.Post(new HelloEvent(), options => options.ResponseFor(request));
                return request.Processed();
            });

    protected MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) => configuration;

    [Fact]
    public async Task SimpleRequest()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(new SayHelloRequest(), o => o.WithTarget(new SubHubAddress(new HostAddress())));
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }
}
