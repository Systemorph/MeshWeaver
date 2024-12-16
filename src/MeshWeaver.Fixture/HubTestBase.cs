using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MeshWeaver.Fixture;

public class HubTestBase : TestBase
{

    protected record RouterAddress;

    protected record HostAddress;

    protected record ClientAddress;

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
        await Router.DisposeAsync();
        Logger.LogInformation("Finished disposal of router");
    }
}
