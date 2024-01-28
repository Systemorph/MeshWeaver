using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;
using Xunit.Abstractions;

namespace OpenSmc.Hub.Fixture;

public class HubTestBase : TestBase
{
    protected record RouterAddress;

    protected record HostAddress;
    protected record ClientAddress;

    [Inject] protected IMessageHub Router;
    protected HubTestBase(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(new RouterAddress(), 
            conf => conf
                .WithForwards(forward => forward
                    .RouteAddressToHostedHub<HostAddress>(ConfigureHost)
                    .RouteAddressToHostedHub<ClientAddress>(ConfigureClient)
                )));
    }

    protected virtual MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) => configuration;
    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) => configuration;

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

