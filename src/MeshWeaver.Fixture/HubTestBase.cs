using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
    // Unique-per-call when id is null. See MonolithMeshTestBase.CreateClientAddress
    // for the routing-table partitioning rationale (leaked server-side sync streams
    // from prior tests' client hubs flooding the latest client/1's action block).
    protected static Address CreateClientAddress(string? id = null) => new(ClientType, id ?? Guid.NewGuid().ToString("N")[..12]);

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
        // 🚨 Never-null AccessContext invariant (feedback_access_context_always_set):
        // these are message-ROUTING/plumbing test fixtures with NO logged-in user — they
        // exercise the courier, not access control. So they post as infrastructure
        // (PostingIdentity.System), exactly like routing/persistence in production. The
        // System mode is applied LAST (outermost wrap of ConfigureHost/ConfigureClient and
        // the mesh itself) so it holds regardless of whether a subclass's ConfigureHost
        // calls base. A test that specifically asserts user-identity / never-null behaviour
        // creates the hub it drives with an explicit PostingIdentity.User config via
        // GetHost/GetClient. (MonolithMeshTestBase, which auto-logs in a user, does NOT
        // inherit this — its posts carry the real user.)
        return conf
            .WithRoutes(forward =>
                forward
                    .RouteAddressToHostedHub(HostType, c => ConfigureHost(c).WithPostingIdentity(PostingIdentity.System))
                    .RouteAddressToHostedHub(ClientType, c => ConfigureClient(c).WithPostingIdentity(PostingIdentity.System))
            )
            .WithPostingIdentity(PostingIdentity.System);
    }

    protected virtual MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) => configuration;

    protected virtual MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => configuration;

    protected virtual IMessageHub GetHost(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        // Default path: plumbing fixture → System posting identity (see ConfigureMesh).
        // Explicit-config path: the caller's config wins (e.g. a test opting into
        // PostingIdentity.User to assert the never-null behaviour).
        return Mesh.GetHostedHub(CreateHostAddress(),
            configuration ?? (c => ConfigureHost(c).WithPostingIdentity(PostingIdentity.System)));
    }

    protected virtual IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        return Mesh.GetHostedHub(CreateClientAddress(),
            configuration ?? (c => ConfigureClient(c).WithPostingIdentity(PostingIdentity.System)));
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

            if (Mesh.IsDisposing)
            {
                Logger.LogInformation("[{DisposalId}] Mesh is disposing, waiting for completion", disposalId);
                // Bridge the reactive completion to a Task once, at this test-teardown edge.
                // Catch folds a disposal fault into completion (teardown waits for "done", not why).
                await Mesh.DisposalCompleted
                    .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
                    .FirstOrDefaultAsync()
                    .ToTask()
                    .WaitAsync(timeout.Token);
                Logger.LogInformation("[{DisposalId}] Mesh disposal completed", disposalId);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogError("[{DisposalId}] HANG DETECTED: Mesh disposal timed out after {TimeoutSeconds}s", disposalId, 10);
            Logger.LogError("[{DisposalId}] Mesh address: {Address}", disposalId, Mesh.Address);
            Logger.LogError("[{DisposalId}] Mesh disposal diagnostics:\n{Diagnostics}",
                disposalId, SafeGetDisposalDiagnostics());

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

    private string SafeGetDisposalDiagnostics()
    {
        try { return Mesh.GetDisposalDiagnostics(); }
        catch (Exception ex) { return $"<failed to gather diagnostics: {ex.Message}>"; }
    }
}
