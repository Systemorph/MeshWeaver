using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ServiceProvider;
using Xunit.Abstractions;

namespace MeshWeaver.Hub.Fixture;

public class HubTestBase : TestBase
{
    protected record RouterAddress;

    protected record HostAddress;

    protected record ClientAddress;

    [Inject]
    protected IMessageHub Router;

    protected HubTestBase(ITestOutputHelper output)
        : base(output)
    {
        Services.AddSingleton(
            (Func<IServiceProvider, IMessageHub>)(
                sp => sp.CreateMessageHub(new RouterAddress(), conf => ConfigureRouter(conf))
            )
        );
    }

    protected virtual MessageHubConfiguration ConfigureRouter(MessageHubConfiguration conf)
    {
        return conf.WithRoutes(forward =>
            forward
                .RouteAddressToHostedHub<HostAddress>(ConfigureHost)
                .RouteAddressToHostedHub<ClientAddress>(ConfigureClient)
        );
    }

    protected virtual MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) => configuration.WithTypes(typeof(HostAddress), typeof(ClientAddress));

    protected virtual MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => configuration.WithTypes(typeof(HostAddress), typeof(ClientAddress));

    protected virtual IMessageHub GetHost()
    {
        return Router.GetHostedHub(new HostAddress(), ConfigureHost);
    }

    protected virtual IMessageHub GetClient()
    {
        return Router.GetHostedHub(new ClientAddress(), ConfigureClient);
    }

    public override async Task DisposeAsync()
    {
        // TODO V10: This should dispose the other two. (18.01.2024, Roland Buergi)
        await Router.DisposeAsync();
    }
}
