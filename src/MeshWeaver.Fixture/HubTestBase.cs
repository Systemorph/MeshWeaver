using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Fixture;

public class HubTestBase : TestBase
{

    protected const string RouterType = "router";
    protected const string HostType = "host";
    protected const string ClientType = "client";

    protected static Address CreateRouterAddress(string? id = null) => new(RouterType, id ?? "1");
    protected static Address CreateHostAddress(string? id = null) => new(HostType, id ?? "1");
    protected static Address CreateClientAddress(string? id = null) => new(ClientType, id ?? "1");

    [Inject]
    protected IMessageHub Router = null!;
    [Inject]
    protected ILogger<HubTestBase> Logger = null!;
    
    protected HubTestBase(ITestOutputHelper output)
        : base(output)
    {

        Services.AddSingleton(
            sp => sp.CreateMessageHub(CreateRouterAddress(), ConfigureRouter)
        );
    }

    protected virtual MessageHubConfiguration ConfigureRouter(MessageHubConfiguration conf)
    {
        return conf.WithRoutes(forward =>
            forward
                .RouteAddressToHostedHub(HostType, ConfigureHost)
                .RouteAddressToHostedHub(ClientType, ConfigureClient)
        );
    }

    protected virtual MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) => configuration;

    protected virtual MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => configuration;

    protected virtual IMessageHub GetHost(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        return Router.GetHostedHub(CreateHostAddress(), configuration ?? ConfigureHost);
    }

    protected virtual IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        return Router.GetHostedHub(CreateClientAddress(), configuration ?? ConfigureClient);
    }
    public override async ValueTask DisposeAsync()
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (Router is null)
            return;
        var disposalId = Guid.NewGuid().ToString("N")[..8];

        Logger.LogInformation("[{DisposalId}] Starting disposal of router {RouterAddress}", disposalId, Router.Address);

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
