using MeshWeaver.Activities;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Fixture;

public class HubTestBase : TestBase
{

    protected record RouterAddress(string? Id = default) : Address("router", Id ?? "1");

    protected record HostAddress(string? Id = default) : Address("host", Id ?? "1");


    protected record ClientAddress(string? Id = default) : Address("client", Id ?? "1");

    [Inject]
    protected IMessageHub Router = null!;
    [Inject]
    protected ILogger<HubTestBase> Logger = null!;
    
    protected HubTestBase(ITestOutputHelper output)
        : base(output)
    {
        // Add debug file logging for message flow tracking
        Services.AddLogging(logging =>
        {
            logging.AddProvider(new DebugFileLoggerProvider());
            logging.SetMinimumLevel(LogLevel.Debug);
        });

        Services.AddSingleton(
            sp => sp.CreateMessageHub(new RouterAddress(), ConfigureRouter)
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

    protected virtual IMessageHub GetHost(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        return Router!.GetHostedHub(new HostAddress(), configuration ?? ConfigureHost)!;
    }

    protected virtual IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        return Router!.GetHostedHub(new ClientAddress(), configuration ?? ConfigureClient)!;
    }
    public override async ValueTask DisposeAsync()
    {
        if (Router is null)
            return;
        var disposalId = Guid.NewGuid().ToString("N")[..8];

        Logger.LogInformation("[{DisposalId}] Starting disposal of router {RouterAddress}", disposalId, Router?.Address);

        try
        {
            // Simple timeout - just enough to detect hangs without aggressive intervention
            var timeoutSeconds = 10;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            // Log which hubs exist before disposal
            var hostedHubsProperty = Router.GetType().GetProperty("HostedHubs");
            var hostedHubsValue = hostedHubsProperty?.GetValue(Router)?.ToString() ?? "unknown";
            Logger.LogInformation("[{DisposalId}] Router has {HubCount} hosted hubs", disposalId, hostedHubsValue);

            if (Router.Disposal is not null)
                await Router.Disposal.WaitAsync(timeout.Token);

            if (Router.Disposal is not null)
            {
                Logger.LogInformation("[{DisposalId}] Router.Disposal exists, waiting for completion", disposalId);
                await Router.Disposal.WaitAsync(timeout.Token);
                Logger.LogInformation("[{DisposalId}] Router.Disposal completed", disposalId);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogError("[{DisposalId}] HANG DETECTED: Router disposal timed out after {TimeoutSeconds}s", disposalId, 10);
            Logger.LogError("[{DisposalId}] Router address: {Address}", disposalId, Router.Address);
            var disposalState = Router.Disposal?.IsCompleted == true ? "Completed" : 
                Router.Disposal?.IsFaulted == true ? "Faulted" : 
                Router.Disposal == null ? "Null" : "Pending";
            Logger.LogError("[{DisposalId}] Router.Disposal state: {State}", disposalId, disposalState);
            
            // Don't fight symptoms - let it timeout and provide diagnostic info
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{DisposalId}] Exception during router disposal", disposalId);
            throw;
        }
        finally
        {
            await base.DisposeAsync();
            Router = null!;
        }
    }
}
