using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Fixture;

/// <summary>
/// Base class for message-routing tests. Sets up a mesh hub with hosted host and client
/// hubs, all posting as <see cref="PostingIdentity.System"/>, so subclasses can exercise
/// courier/routing behaviour without an authenticated user.
/// </summary>
public class HubTestBase : TestBase
{

    /// <summary>The address type used for the mesh (router) hub.</summary>
    protected const string MeshType = AddressExtensions.MeshType;
    /// <summary>The address type used for hosted host hubs.</summary>
    protected const string HostType = "host";
    /// <summary>The address type used for hosted client hubs.</summary>
    protected const string ClientType = "client";

    /// <summary>Creates a mesh hub address, defaulting the id to <c>"1"</c>.</summary>
    /// <param name="id">Optional address id; defaults to <c>"1"</c> when null.</param>
    /// <returns>The mesh hub <see cref="Address"/>.</returns>
    protected static Address CreateMeshAddress(string? id = null) => new(MeshType, id ?? "1");
    /// <summary>Creates a host hub address, defaulting the id to <c>"1"</c>.</summary>
    /// <param name="id">Optional address id; defaults to <c>"1"</c> when null.</param>
    /// <returns>The host hub <see cref="Address"/>.</returns>
    protected static Address CreateHostAddress(string? id = null) => new(HostType, id ?? "1");
    /// <summary>
    /// Creates a client hub address. When <paramref name="id"/> is null a unique id is
    /// generated per call to avoid leaked server-side sync streams from prior tests'
    /// client hubs flooding a shared client address's action block.
    /// </summary>
    /// <param name="id">Optional address id; a unique id is generated when null.</param>
    /// <returns>The client hub <see cref="Address"/>.</returns>
    // Unique-per-call when id is null. See MonolithMeshTestBase.CreateClientAddress
    // for the routing-table partitioning rationale (leaked server-side sync streams
    // from prior tests' client hubs flooding the latest client/1's action block).
    protected static Address CreateClientAddress(string? id = null) => new(ClientType, id ?? Guid.NewGuid().ToString("N")[..12]);

    /// <summary>The mesh (router) hub injected by the fixture's service provider.</summary>
    [Inject]
    protected IMessageHub Mesh = null!;
    /// <summary>The logger for this test base, injected by the fixture's service provider.</summary>
    [Inject]
    protected ILogger<HubTestBase> Logger = null!;

    /// <summary>
    /// Initializes a new instance and registers the mesh hub in the service collection.
    /// </summary>
    /// <param name="output">The xUnit output helper for the running test.</param>
    protected HubTestBase(ITestOutputHelper output)
        : base(output)
    {

        Services.AddSingleton(
            sp => sp.CreateMessageHub(CreateMeshAddress(), ConfigureMesh)
        );
    }

    /// <summary>
    /// Configures the mesh hub, wiring routes to hosted host and client hubs and applying
    /// the <see cref="PostingIdentity.System"/> posting identity for plumbing-only tests.
    /// </summary>
    /// <param name="conf">The mesh hub configuration to extend.</param>
    /// <returns>The extended configuration.</returns>
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

    /// <summary>
    /// Hook for subclasses to configure the hosted host hub. The base implementation returns
    /// the configuration unchanged.
    /// </summary>
    /// <param name="configuration">The host hub configuration to extend.</param>
    /// <returns>The (possibly extended) configuration.</returns>
    protected virtual MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) => configuration;

    /// <summary>
    /// Hook for subclasses to configure the hosted client hub. The base implementation returns
    /// the configuration unchanged.
    /// </summary>
    /// <param name="configuration">The client hub configuration to extend.</param>
    /// <returns>The (possibly extended) configuration.</returns>
    protected virtual MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => configuration;

    /// <summary>
    /// Resolves the hosted host hub, applying the default System posting identity unless an
    /// explicit configuration is supplied.
    /// </summary>
    /// <param name="configuration">Optional configuration override; the caller's config wins when supplied.</param>
    /// <returns>The hosted host <see cref="IMessageHub"/>.</returns>
    protected virtual IMessageHub GetHost(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        // Default path: plumbing fixture → System posting identity (see ConfigureMesh).
        // Explicit-config path: the caller's config wins (e.g. a test opting into
        // PostingIdentity.User to assert the never-null behaviour).
        return Mesh.GetHostedHub(CreateHostAddress(),
            configuration ?? (c => ConfigureHost(c).WithPostingIdentity(PostingIdentity.System)));
    }

    /// <summary>
    /// Resolves a hosted client hub at a unique address, applying the default System posting
    /// identity unless an explicit configuration is supplied.
    /// </summary>
    /// <param name="configuration">Optional configuration override; the caller's config wins when supplied.</param>
    /// <returns>The hosted client <see cref="IMessageHub"/>.</returns>
    protected virtual IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = default)
    {
        return Mesh.GetHostedHub(CreateClientAddress(),
            configuration ?? (c => ConfigureClient(c).WithPostingIdentity(PostingIdentity.System)));
    }
    /// <summary>
    /// Disposes the mesh hub with a 10 second hang-detection timeout, logging diagnostics if
    /// disposal does not complete in time, then chains to the base disposal.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that completes when disposal finishes.</returns>
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

            if (!Mesh.IsDisposing)
            {
                // The fixture owns the mesh, so the fixture must tear it down. This initiation
                // was lost in aad01631a ("proper await in TestFixture"); since then every
                // HubTestBase test leaked its live mesh (hubs, layout hosts, subscriptions)
                // past the test. A leaked debounced auto-save subscription then wrote to the
                // finished test's raw ITestOutputHelper → xUnit "There is no currently active
                // test." → anonymous catastrophic failure → all-green trx but exit 1
                // (Layout.Test red on every CI run, masked by the trx-only gate).
                Logger.LogInformation("[{DisposalId}] Initiating mesh disposal", disposalId);
                Mesh.Dispose();
            }

            Logger.LogInformation("[{DisposalId}] Mesh is disposing, waiting for completion", disposalId);
            // Bridge the reactive completion to a Task once, at this test-teardown edge.
            // Catch folds a disposal fault into completion (teardown waits for "done", not why).
            // DisposalCompleted replays to late subscribers, so this is race-free.
            await Mesh.DisposalCompleted
                .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
                .FirstOrDefaultAsync()
                .ToTask()
                .WaitAsync(timeout.Token);
            Logger.LogInformation("[{DisposalId}] Mesh disposal completed", disposalId);
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
