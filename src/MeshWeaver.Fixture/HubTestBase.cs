using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Fixture;

public class HubTestBase : TestBase
{

    protected const string MeshType = AddressExtensions.MeshType;
    protected const string HostType = "host";
    protected const string ClientType = "client";

    protected static Address CreateMeshAddress(string? id = null) => new(MeshType, id ?? "1");
    protected static Address CreateHostAddress(string? id = null) => new(HostType, id ?? "1");
    protected static Address CreateClientAddress(string? id = null) => new(ClientType, id ?? "1");

    [Inject]
    protected IMessageHub Mesh = null!;
    [Inject]
    protected ILogger<HubTestBase> Logger = null!;

    protected HubTestBase(ITestOutputHelper output)
        : base(output)
    {

        Services.AddSingleton(
            sp => sp.CreateMessageHub(CreateMeshAddress(), ConfigureMesh)
        );
    }

    protected virtual MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
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
        return Mesh.GetHostedHub(CreateHostAddress(), configuration ?? ConfigureHost);
    }

    protected virtual IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        return Mesh.GetHostedHub(CreateClientAddress(), configuration ?? ConfigureClient);
    }
    public override async ValueTask DisposeAsync()
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (Mesh is null)
            return;
        var disposalId = Guid.NewGuid().ToString("N")[..8];

        Logger.LogInformation("[{DisposalId}] Starting disposal of router {RouterAddress}", disposalId, Mesh.Address);

        try
        {
            // Simple timeout - just enough to detect hangs without aggressive intervention
            var timeoutSeconds = 10;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            // Log which hubs exist before disposal
            var hostedHubsProperty = Mesh.GetType().GetProperty("HostedHubs");
            var hostedHubsValue = hostedHubsProperty?.GetValue(Mesh)?.ToString() ?? "unknown";
            Logger.LogInformation("[{DisposalId}] Mesh has {HubCount} hosted hubs", disposalId, hostedHubsValue);

            if (Mesh.Disposal is not null)
                await Mesh.Disposal.WaitAsync(timeout.Token);

            if (Mesh.Disposal is not null)
            {
                Logger.LogInformation("[{DisposalId}] Mesh.Disposal exists, waiting for completion", disposalId);
                await Mesh.Disposal.WaitAsync(timeout.Token);
                Logger.LogInformation("[{DisposalId}] Mesh.Disposal completed", disposalId);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogError("[{DisposalId}] HANG DETECTED: Mesh disposal timed out after {TimeoutSeconds}s", disposalId, 10);
            Logger.LogError("[{DisposalId}] Mesh address: {Address}", disposalId, Mesh.Address);
            var disposalState = Mesh.Disposal?.IsCompleted == true ? "Completed" :
                Mesh.Disposal?.IsFaulted == true ? "Faulted" :
                Mesh.Disposal == null ? "Null" : "Pending";
            Logger.LogError("[{DisposalId}] Mesh.Disposal state: {State}", disposalId, disposalState);

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
            Mesh = null!;
        }
    }
}
