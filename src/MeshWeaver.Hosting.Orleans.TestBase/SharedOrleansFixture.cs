using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.Hosting.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans TestCluster fixture. Despite the name it is no longer shared across the assembly:
/// <see cref="OrleansSharedTestBase"/> constructs one per test class, so grain state is
/// isolated by construction.
///
/// <para>Configuration: production-like (Graph + RLS + memory persistence), and deliberately
/// FREE of any module's types. A repo that ships a module uses the generic form with its own
/// silo configurator, and overrides the client hook to add its types — which is
/// how the AI rig lives in MeshWeaver.Plugins while the ~51 Orleans tests that never touch an
/// agent keep using this one unchanged (#2276).</para>
/// </summary>
public class SharedOrleansFixture : IAsyncLifetime
{
    private OrleansTestClusterHost host = null!;

    public TestCluster Cluster => host.Cluster;
    public IMessageHub ClientMesh => host.ClientServices.GetRequiredService<IMessageHub>();

    /// <summary>
    /// The Orleans client host's services. <c>Cluster.Client</c> is null on these clusters —
    /// the client host belongs to <see cref="OrleansTestClusterHost"/> so it can be handed
    /// per-cluster instances through a closure. See that type's remarks.
    /// </summary>
    public IServiceProvider ClientServices => host.ClientServices;

    /// <summary>The Orleans cluster client (grain factory) — <c>Cluster.Client</c>'s replacement.</summary>
    public IClusterClient ClusterClient => host.ClusterClient;

    /// <summary>
    /// Per-client tracker: every hub returned by <see cref="GetClient"/>
    /// gets recorded along with its routing-stream subscriptions on both the
    /// client mesh and the silo mesh. Tests dispose these in
    /// <c>OrleansSharedTestBase.DisposeAsync</c> so the shared cluster's
    /// stream registries and hosted-hub collection don't grow unboundedly
    /// across the test run.
    /// </summary>
    private readonly ConcurrentDictionary<Address, ClientRegistration> _registrations = new();

    private sealed record ClientRegistration(IMessageHub Hub, IReadOnlyList<IDisposable> Subscriptions);

    /// <summary>
    /// Subclass hook: the silo configurator this cluster is built with. Orleans instantiates it via
    /// <c>new()</c>, so a subclass contributes behaviour by naming a DERIVED type here rather than
    /// by handing over an instance.
    /// </summary>
    protected virtual Type SiloConfiguratorType => typeof(SharedSiloConfigurator);

    /// <summary>
    /// The silo container, for a subclass that registered its own services through its silo
    /// configurator and needs to read one back (per-cluster by construction).
    /// </summary>
    protected IServiceProvider SiloServices() => host.SiloServices();

    /// <summary>
    /// Subclass hook: extra client-hub configuration — typically registering a module's content
    /// types in the client TypeRegistry so its nodes deserialize on the client side. Default is
    /// unchanged.
    /// </summary>
    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration config) => config;

    public async ValueTask InitializeAsync()
    {
        host = await OrleansTestCluster.DeployAsync(
            builder =>
            {
                // Single-silo: avoids per-silo persistence isolation. Both writer (mesh hub)
                // and reader (per-Thread/per-Message grain) share the same singleton
                // InMemoryStorageAdapter so the grain's OnActivateAsync persistence
                // lookup finds the node the mesh hub just saved. Production runs N silos
                // with backend-shared persistence (PostgreSQL / Cosmos) which doesn't have
                // this issue; the in-memory test cluster does.
                builder.Options.InitialSilosCount = 1;
                // Added by TYPE NAME rather than through AddSiloBuilderConfigurator<T>(), which
                // needs a compile-time argument: a subclass in another repo contributes its own
                // configurator by overriding SiloConfiguratorType, and that is what lets the AI rig
                // live in MeshWeaver.Plugins while this fixture stays free of AI types (#2276).
                builder.Options.SiloBuilderConfiguratorTypes.Add(
                    SiloConfiguratorType.AssemblyQualifiedName!);
                builder.AddClientBuilderConfigurator<TestClientConfigurator>();
            },
            // The Orleans client borrows the silo's InMemoryStorageAdapter so the two hosts are
            // one logical store — the shape prod gets for free from a shared PG database.
            configureClientServices: OrleansTestCluster.ShareSiloNodeStore);

        // 🚨 Register the client's mesh hub as an Orleans memory-stream
        // subscriber so the silo can route response messages back to it.
        // The client mesh hub at `mesh/{guid}` isn't a grain — it's a
        // hosted hub on the client process. Without this registration, a
        // SubscribeRequest the client mesh posts to a remote path (e.g.
        // GetMeshNodeStream(remotePath).Update) gets handled on the silo,
        // but the response is targeted back to `mesh/{guid}` which the
        // silo's RoutingGrain can't resolve → NotFound. RegisterStream
        // subscribes the hub to the memory stream so the silo's RoutingGrain
        // memory-stream fallback delivers responses correctly.
        ClientServices.GetRequiredService<IRoutingService>()
            .RegisterStream(ClientMesh.Address, ClientMesh.DeliverMessage);
    }

    public ValueTask DisposeAsync()
    {
        OrleansClusterDisposal.DisposeInBackground(host);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates a client hub with user identity — same as Blazor portal.
    /// Each test should use a unique clientId to avoid address collisions.
    /// The returned hub is tracked; pass it to <see cref="CleanupClientAsync"/>
    /// at test teardown so its routing registrations on the shared cluster
    /// (client + silo mesh) and the hub itself are released.
    /// </summary>
    public IMessageHub GetClient(string clientId, string userId = "TestUser")
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", clientId),
            config => ConfigureClient(config).AddLayoutClient());
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetHostIdentity(new AccessContext
        {
            ObjectId = userId,
            Name = userId,
            Email = $"{userId.ToLowerInvariant()}@test.com"
        });
        var subscriptions = new List<IDisposable>(2);

        // Register on BOTH client and silo routing services so responses can route back
        var clientSub = ClientServices.GetRequiredService<IRoutingService>()
            .RegisterStream(client.Address, client.DeliverMessage);
        subscriptions.Add(clientSub);

        // Register on the SILO's routing service so responses route back to client.
        // In prod, portal and silo share one IRoutingService. In TestCluster they're separate.
        // Without this, response routing tries to activate a grain for the client address → fails.
        // Access silo's IRoutingService via reflection (InProcessSiloHandle.SiloHost.Services)
        // Try multiple paths to find the silo's IRoutingService
        var primarySilo = Cluster.Primary;
        var siloHost = primarySilo.GetType().GetProperty("SiloHost")?.GetValue(primarySilo) as IHost;
        var siloRouting = siloHost?.Services.GetService<IRoutingService>()
            ?? siloHost?.Services.GetService<IMessageHub>()?.ServiceProvider.GetService<IRoutingService>();
        if (siloRouting != null)
        {
            var siloSub = siloRouting.RegisterStream(client.Address,
                (d, _) => Observable.Return(client.DeliverMessage(d)));
            subscriptions.Add(siloSub);
        }

        _registrations[client.Address] = new ClientRegistration(client, subscriptions);
        return client;
    }

    /// <summary>
    /// Releases the routing-stream registrations and disposes the client hub
    /// returned by <see cref="GetClient"/>. Idempotent: safe to call
    /// twice and safe to call on an unknown client (e.g., after the fixture
    /// itself disposed). Tests should call this from <c>DisposeAsync</c> for
    /// every client they created so the shared cluster's stream maps and
    /// hosted-hub collection don't accumulate dead entries between tests.
    /// </summary>
    public async Task CleanupClientAsync(IMessageHub client)
    {
        if (client is null) return;
        if (!_registrations.TryRemove(client.Address, out var reg)) return;

        foreach (var sub in reg.Subscriptions)
        {
            try { sub.Dispose(); }
            catch { /* tearing-down — swallow so other cleanups still run */ }
        }

        // Captured while the hub's scope is still alive — a late fault has to land SOMEWHERE, and
        // "somewhere" cannot be a service resolved after disposal began (the same rule
        // MeshTeardownExtensions states for every teardown service).
        var logger = SafeLogger(reg.Hub);

        try { reg.Hub.Dispose(); }
        catch { /* same */ }
        if (reg.Hub.IsDisposing)
        {
            // 🚨 SUBSCRIBE; bound the WAIT with a token, never a Timeout spliced into the signal
            // (#2301/#2488). The old shape — `Catch(…).FirstOrDefaultAsync().ToTask().WaitAsync(10s)`
            // inside a bare `catch { }` — resumed this cleanup INLINE on the client hub's own
            // disposal thread (Rx completes a ToTask() TCS without RunContinuationsAsynchronously),
            // and then discarded three different outcomes as one silence: a disposal fault, a fault
            // arriving after the bridge settled, and the budget expiring. Cleanup still proceeds on
            // all three (a test fixture must not hang on a client hub), but it no longer rides a hub
            // thread to get there, and each outcome is now SAID.
            using var disposalBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                // ConfigureAwait(false) on top of ObserveCompletion's
                // RunContinuationsAsynchronously: `await` captures TaskScheduler.Current absent a
                // SynchronizationContext, so cleanup entered from a hub scheduler would otherwise
                // carry the rest of itself back onto one. (Copilot review, #2527.)
                await reg.Hub.DisposalCompleted.ObserveCompletion(
                    ex => logger?.LogError(ex,
                        "Client hub {Address}: disposal faulted AFTER cleanup stopped waiting on it "
                        + "— reported rather than orphaned.", reg.Hub.Address),
                    disposalBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger?.LogError(
                    "Client hub {Address}: disposal did not complete within 10s during test cleanup. "
                    + "Something on its action block is not finishing.", reg.Hub.Address);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Client hub {Address}: disposal FAULTED during test cleanup.",
                    reg.Hub.Address);
            }
        }
    }

    /// <summary>
    /// A logger from a hub whose scope may be mid-teardown — resolved defensively so cleanup can
    /// never fail on the act of preparing to REPORT something.
    /// </summary>
    private static ILogger? SafeLogger(IMessageHub hub)
    {
        try
        {
            return hub.ServiceProvider.GetService<ILoggerFactory>()?
                .CreateLogger("MeshWeaver.Hosting.Orleans.Test.Cleanup");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort: disposes silo-side hosted hubs whose address path starts
    /// with the given prefix. Used to deactivate the per-node grain hubs a
    /// single test created (its unique <c>{prefix}</c> in test paths) so they
    /// don't keep state alive into the next test.
    /// <br/>
    /// Grain disposal flows through <c>MessageHubGrain</c>: disposing the
    /// hosted hub triggers <c>DeactivateOnIdle()</c> on the owning grain
    /// (see <c>MessageHubGrain.OnActivateAsync</c>).
    /// </summary>
    public void CleanupSiloHubsWithPrefix(string pathPrefix)
    {
        if (string.IsNullOrEmpty(pathPrefix)) return;
        foreach (var siloHandle in Cluster.Silos)
        {
            var siloHost = siloHandle.GetType().GetProperty("SiloHost")?.GetValue(siloHandle) as IHost;
            var meshHub = siloHost?.Services.GetService<IMessageHub>();
            if (meshHub is null) continue;

            // hostedHubs is private; reach it via reflection (test-only).
            var field = meshHub.GetType().GetField("hostedHubs", BindingFlags.Instance | BindingFlags.NonPublic);
            var hosted = field?.GetValue(meshHub) as HostedHubsCollection;
            if (hosted is null) continue;

            foreach (var hub in hosted.Hubs.ToArray())
            {
                if (hub.Address.ToString().StartsWith(pathPrefix, StringComparison.Ordinal))
                {
                    try { hub.Dispose(); } catch { /* swallow */ }
                }
            }
        }
    }
}

/// <summary>
/// Production-like silo: Graph + RLS + memory persistence.
/// Pre-seeds the TestUser user and its access grant via
/// <see cref="OrleansTestSeedProvider"/> (an <see cref="IStaticNodeProvider"/>)
/// so the seeds are an immutable activation fallback rather than an initial
/// snapshot that tests could mutate or rewrite via persistence.
/// </summary>
public class SharedSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault()
            .ConfigureLogging(logging => logging.AddXUnitLogger());
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        var configured = hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh()
            .AddGraph()
            .AddRowLevelSecurity()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStaticNodeProvider, OrleansTestSeedProvider>();
                return services;
            });
        ConfigureAdditional(configured)
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Subclass hook: registrations a module-owning repo adds to the silo — the slot
    /// <c>.AddAI()</c> and the chat-factory singletons used to occupy inline. Default is
    /// unchanged, which is what keeps core's agent-free Orleans tests on the plain configurator.
    /// </summary>
    protected virtual MeshBuilder ConfigureAdditional(MeshBuilder builder) => builder;
}
