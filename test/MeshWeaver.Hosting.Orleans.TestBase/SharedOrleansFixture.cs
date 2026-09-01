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
/// <see cref="OrleansMeshTestBase"/> leases or constructs one per test class, so grain state is
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
    /// <c>OrleansMeshTestBase.DisposeAsync</c> so the shared cluster's
    /// stream registries and hosted-hub collection don't grow unboundedly
    /// across the test run.
    /// </summary>
    private readonly ConcurrentDictionary<Address, ClientRegistration> _registrations = new();

    private sealed record ClientRegistration(IMessageHub Hub, IReadOnlyList<IDisposable> Subscriptions);

    /// <summary>
    /// The cluster shape this fixture was asked for, or <c>null</c> when it was constructed the
    /// old way (a DERIVED fixture that answers the hooks below by overriding them).
    ///
    /// <para>🚨 A derived fixture's override WINS. That is what keeps a repo-owned rig — the AI
    /// one in MeshWeaver.Plugins — working unchanged while <see cref="OrleansMeshTestBase"/>
    /// drives the shape from the suite's <see cref="IMeshBootstrap"/> instead of from a second
    /// fixture subclass per silo configurator.</para>
    /// </summary>
    private readonly OrleansClusterShape? shape;

    /// <summary>Constructs the default cluster — one silo, the shared configurator.</summary>
    public SharedOrleansFixture() { }

    /// <summary>Constructs the cluster a caller DESCRIBED, rather than one a subclass hard-codes.</summary>
    public SharedOrleansFixture(OrleansClusterShape shape) => this.shape = shape;

    /// <summary>
    /// Subclass hook: the silo configurator this cluster is built with. Orleans instantiates it via
    /// <c>new()</c>, so a subclass contributes behaviour by naming a DERIVED type here rather than
    /// by handing over an instance.
    /// </summary>
    protected virtual Type SiloConfiguratorType => shape?.SiloConfigurator ?? typeof(SharedSiloConfigurator);

    /// <summary>
    /// Subclass hook: the CLIENT configurator. Separate from the silo's on purpose — the client
    /// mesh is its own hub and needs its own registrations (see TestClientConfigurator.MeshExtra).
    /// </summary>
    protected virtual Type ClientConfiguratorType => shape?.ClientConfigurator ?? typeof(TestClientConfigurator);

    /// <summary>
    /// How many silos this cluster starts with. Comes from the suite's
    /// <c>MeshBootstrap.Orleans(o =&gt; o.WithSilos(n))</c>; one unless asked otherwise.
    /// </summary>
    protected virtual short InitialSilosCount => shape?.Silos ?? 1;

    /// <summary>
    /// Whether the Orleans CLIENT borrows the SILO's <see cref="InMemoryStorageAdapter"/>, making
    /// the two hosts one logical store — prod's "several adapter instances, one PG database".
    ///
    /// <para>🚨 False for a configurator that brings its OWN durable backend (FileSystem, the
    /// PostgreSQL ones): those claim at Priority 100 while the in-memory catch-all sits at 0, so
    /// joining the stores would let a client-side mirror answer a read the durable backend owns.</para>
    /// </summary>
    protected virtual bool ClientSharesSiloStore => shape?.ShareSiloStore ?? true;

    /// <summary>
    /// What makes two clusters INTERCHANGEABLE, and therefore what <see cref="OrleansMeshPool"/>
    /// leases on. 🚨 The fixture TYPE alone is not enough any more: since the shape can be
    /// described rather than subclassed, two suites can both want a plain
    /// <see cref="SharedOrleansFixture"/> and mean different silos.
    /// </summary>
    public OrleansClusterShape PoolKey =>
        new(SiloConfiguratorType, ClientConfiguratorType, InitialSilosCount, ClientSharesSiloStore)
        { FixtureType = GetType() };

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
                builder.Options.InitialSilosCount = InitialSilosCount;
                // Added by TYPE NAME rather than through AddSiloBuilderConfigurator<T>(), which
                // needs a compile-time argument: a subclass in another repo contributes its own
                // configurator by overriding SiloConfiguratorType, and that is what lets the AI rig
                // live in MeshWeaver.Plugins while this fixture stays free of AI types (#2276).
                builder.Options.SiloBuilderConfiguratorTypes.Add(
                    SiloConfiguratorType.AssemblyQualifiedName!);
                builder.Options.ClientBuilderConfiguratorTypes.Add(
                    ClientConfiguratorType.AssemblyQualifiedName!);
            },
            // The Orleans client borrows the silo's InMemoryStorageAdapter so the two hosts are
            // one logical store — the shape prod gets for free from a shared PG database. A
            // configurator with its OWN durable backend opts out; see ClientSharesSiloStore.
            configureClientServices: ClientSharesSiloStore ? OrleansTestCluster.ShareSiloNodeStore : null);

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
        => GetClient(clientId, userId, null);

    /// <summary>
    /// As <see cref="GetClient(string,string)"/>, plus the SUITE's own client configuration.
    ///
    /// <para>🚨 Two client shapes were maintained here and in the retired <c>OrleansTestBase</c>:
    /// this one added only <c>AddLayoutClient</c>, that one also added <c>AddMeshDataSource</c>
    /// (the <c>MeshNodeReference</c> reducer + <c>GetDataRequest</c> handler a client needs to
    /// read nodes). The union is what every caller gets now — the data source is additive, and a
    /// client that cannot answer <c>GetDataRequest</c> is the more surprising of the two.</para>
    /// </summary>
    public IMessageHub GetClient(
        string clientId,
        string userId,
        Func<MessageHubConfiguration, MessageHubConfiguration>? configureSuiteClient)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", clientId),
            config => (configureSuiteClient ?? (c => c))(ConfigureClient(config))
                .AddMeshDataSource(source => source)
                .AddLayoutClient());
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
                if (!hub.Address.ToString().StartsWith(pathPrefix, StringComparison.Ordinal))
                    continue;
                // 🚨 JOIN, don't just start it. This method's whole purpose is that the NEXT test
                // does not inherit these hubs' state — and a bare Dispose() gives it exactly that,
                // because disposal is a state machine that returns immediately and drains
                // afterwards. Worse, these are SILO-SIDE hubs: their teardown deactivates the owning
                // grain (MessageHubGrain), so returning early lets the next test's grain calls race
                // a deactivation in flight on a hub it can still address. The wait is a synchronous
                // block-join because this method has no async caller to suspend (it is invoked from
                // synchronous per-test cleanup); it is bounded, and an expiry is written to the
                // fixture's logger rather than swallowed the way `catch { }` used to swallow both a
                // dispose fault and a hung teardown as one silence.
                // Captured while the hub's scope is still alive — never resolve DI once disposal
                // has begun (the same rule CleanupClientAsync above states).
                var logger = SafeLogger(hub);
                hub.DisposeAndJoin(
                    message => logger?.LogError("Silo hub cleanup: {Message}", message),
                    TimeSpan.FromSeconds(10));
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
        // 🚨 ORDER IS LOAD-BEARING and was measured, not reasoned about. The hook fires exactly
        // where `.AddAI()` used to sit — after AddGraph, before AddRowLevelSecurity — and MeshExtra
        // covers the OTHER call it made, inside ConfigurePortalMesh. It was added in BOTH places;
        // keeping only one leaves 2 tests failing with "NodeType 'ModelProvider' is not registered".
        ConfigureAdditional(
                hostBuilder.UseOrleansMeshServer()
                    .ConfigurePortalMesh(MeshExtra)
                    .AddGraph())
            .AddRowLevelSecurity()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStaticNodeProvider, OrleansTestSeedProvider>();
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Subclass hook: registrations a module-owning repo adds to the silo — the DI singletons
    /// <c>.AddAI()</c>'s chat factory used to occupy inline. Default is unchanged.
    /// </summary>
    protected virtual MeshBuilder ConfigureAdditional(MeshBuilder builder) => builder;

    /// <summary>
    /// Subclass hook: mesh registrations, passed INTO <c>ConfigurePortalMesh</c>.
    ///
    /// <para>🚨 Position matters, and getting it wrong is silent: <c>.AddAI()</c> used to sit
    /// inside <c>ConfigurePortalMesh</c> between <c>AddGraph()</c> and <c>AddKernel()</c>. Adding
    /// it AFTER that call instead compiles, boots, and then fails with
    /// <c>NodeType 'ModelProvider' is not registered</c> — measured, 2 tests, 2026-08-28.</para>
    /// </summary>
    protected virtual Func<MeshBuilder, MeshBuilder>? MeshExtra => null;
}

/// <summary>
/// WHAT a test cluster is, reduced to the values that make two of them interchangeable.
///
/// <para>🚨 This is the record <see cref="OrleansMeshPool"/> leases on. Before it, the pool keyed
/// on the fixture TYPE — which was only sound while "a different cluster" and "a different fixture
/// subclass" were the same statement. They stopped being the same the moment a suite could DESCRIBE
/// its cluster (<c>MeshBootstrap.Orleans(o =&gt; o.WithSilos(2))</c>) instead of subclassing a
/// fixture for it, and handing a suite a cluster built by someone else's silo configurator is a
/// failure that reads as a missing registration two files away.</para>
/// </summary>
/// <param name="SiloConfigurator">The silo configurator Orleans <c>new()</c>s for every silo.</param>
/// <param name="ClientConfigurator">The Orleans CLIENT host's configurator.</param>
/// <param name="Silos">How many silos the cluster starts with.</param>
/// <param name="ShareSiloStore">Whether the client borrows the silo's in-memory node store.</param>
public sealed record OrleansClusterShape(
    Type SiloConfigurator,
    Type ClientConfigurator,
    short Silos,
    bool ShareSiloStore)
{
    /// <summary>
    /// The fixture CLASS, so a repo-owned derived fixture never leases a base-fixture cluster.
    /// Not a constructor parameter: a caller DESCRIBING a cluster does not know which fixture class
    /// will end up carrying it.
    /// </summary>
    public Type FixtureType { get; init; } = typeof(SharedOrleansFixture);
}
