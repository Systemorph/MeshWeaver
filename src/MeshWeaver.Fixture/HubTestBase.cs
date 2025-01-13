using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MeshWeaver.Fixture;

public class HubTestBase : TestBase
{

    protected record RouterAddress() : Address("router", "1");

    protected record HostAddress() : Address("host", "1");


    protected record ClientAddress() : Address("client", "1");

    [Inject]
    protected IMessageHub Router;
    [Inject]
    protected ILogger<HubTestBase> Logger;

    protected HubTestBase(ITestOutputHelper output)
        : base(output)
    {
        Services.AddSingleton(
            (Func<IServiceProvider, IMessageHub>)(
                sp => sp.CreateMessageHub(new RouterAddress(), ConfigureRouter)
            )
        );
    }
    private static readonly Dictionary<string, Type> AddressTypes = new ()
    {
        { new ClientAddress().Type, typeof(ClientAddress) },
        { new HostAddress().Type, typeof(HostAddress) },
        { new RouterAddress().Type, typeof(RouterAddress) }
    };
    protected virtual MessageHubConfiguration ConfigureRouter(MessageHubConfiguration conf)
    {
        return conf.WithRoutes(forward =>
            forward
                .RouteAddressToHostedHub<HostAddress>(ConfigureHost)
                .RouteAddressToHostedHub<ClientAddress>(ConfigureClient)
        ).WithTypes(AddressTypes);
    }

    protected virtual MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) => configuration.WithTypes(AddressTypes);

    protected virtual MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => configuration.WithTypes(AddressTypes);

    protected virtual IMessageHub GetHost(Func<MessageHubConfiguration, MessageHubConfiguration> configuration = null)
    {
        return Router.GetHostedHub(new HostAddress(), configuration ?? ConfigureHost);
    }

    protected virtual IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration> configuration = null)
    {
        return Router.GetHostedHub(new ClientAddress(), configuration ?? ConfigureClient);
    }

    public override async Task DisposeAsync()
    {
        Logger.LogInformation("Starting disposal of router");   
        Router.Dispose();
        await Router.Disposed;
        Logger.LogInformation("Finished disposal of router");
    }
}
