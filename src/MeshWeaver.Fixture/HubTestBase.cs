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
    protected ILogger<HubTestBase> Logger; protected HubTestBase(ITestOutputHelper output)
        : base(output)
    {
        // Add debug file logging for message flow tracking
        Services.AddLogging(logging =>
        {
            logging.AddProvider(new DebugFileLoggerProvider());
            logging.SetMinimumLevel(LogLevel.Debug);
        });

        Services.AddSingleton(
            (Func<IServiceProvider, IMessageHub>)(
                sp => sp.CreateMessageHub(new RouterAddress(), ConfigureRouter)
            )
        );
    }
    private static readonly Dictionary<string, Type> AddressTypes = new()
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
    }
    public override async Task DisposeAsync()
    {
        Logger.LogInformation("Starting disposal of router");

        // Log debug file location
        var tempDir = Environment.GetEnvironmentVariable("TEMP") ?? ".";
        var debugLogDir = Path.Combine(tempDir, "MeshWeaverDebugLogs");
        Logger.LogInformation("Debug logs are written to: {DebugLogDir}", debugLogDir);

        try
        {
            // Add aggressive 3 second timeout to prevent hanging
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            // If Router.Disposal is null, don't wait - just dispose synchronously
            if (Router.Disposal != null)
            {
                try
                {
                    await Router.Disposal.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogWarning("Router disposal timed out after 3 seconds - forcing synchronous disposal");
                }
            }

            // Force dispose the router synchronously
            Router.Dispose();

            Logger.LogInformation("Finished disposal of router");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during router disposal - continuing");
            // Don't throw, just log and continue to prevent test hanging
        }
        finally
        {
            // Call base disposal to clean up other resources
            await base.DisposeAsync();
        }
    }
}
