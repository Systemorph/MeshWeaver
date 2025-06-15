using MeshWeaver.Activities;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MeshWeaver.Fixture;

public class HubTestBase : TestBase
{

    protected record RouterAddress(string Id = null) : Address("router", Id ?? "1");

    protected record HostAddress(string Id = null) : Address("host", Id ?? "1");


    protected record ClientAddress(string Id = null) : Address("client", Id ?? "1");

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
    }    private static readonly Dictionary<string, Type> AddressTypes = new()
    {
        { new ClientAddress().Type, typeof(ClientAddress) },
        { new HostAddress().Type, typeof(HostAddress) },
        { new RouterAddress().Type, typeof(RouterAddress) },
        { new ActivityAddress().Type, typeof(ActivityAddress) }
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
    }    public override async Task DisposeAsync()
    {
        Logger.LogInformation("Starting disposal of router");
        
        try
        {
            // Force dispose the router synchronously first
            Router.Dispose();
            
            // Add aggressive 5 second timeout to prevent hanging
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            
            // If Router.Disposal is null, don't wait
            if (Router.Disposal != null)
            {
                await Router.Disposal.WaitAsync(timeout.Token);
            }
            
            Logger.LogInformation("Finished disposal of router");
        }
        catch (OperationCanceledException)
        {
            Logger.LogError("Router disposal timed out after 5 seconds - forcing completion");
            // Don't throw, just log and continue to prevent test hanging
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during router disposal - continuing");
            // Don't throw, just log and continue
        }
    }
}
