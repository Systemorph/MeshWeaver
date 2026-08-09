using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.TestingHost;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A mesh-node store shared by several hosts of ONE test cluster. Production runs
/// PostgreSQL, where several adapter instances point at the same database; the test cluster
/// needs the same shape so a node written through one host is visible to another's path
/// resolver.
///
/// <para>🚨 This is an INSTANCE, created and owned by the fixture that deploys the cluster,
/// and it dies with it. It replaces the process-wide <c>SharedNodes</c> /
/// <c>SharedPartitionObjects</c> statics and the <c>ResetSharedState()</c> that wiped them at
/// the start of every cluster init. That wipe WAS the isolation, and it is what made the
/// assembly un-parallelisable: class B's init cleared the dictionaries while class A was
/// mid-test. See AGENTS.md → "No static collections" and issue #999.</para>
///
/// <para>Only <see cref="TwoSiloCacheUpdateFixture"/> needs this type. Everywhere else the
/// per-cluster store is simply the SILO's own <see cref="InMemoryStorageAdapter"/> DI
/// singleton — already per-cluster by construction — which the Orleans client borrows via
/// <see cref="OrleansTestCluster.ShareSiloNodeStore"/>.</para>
/// </summary>
internal sealed class OrleansTestBackingStore
{
    private readonly ConcurrentDictionary<string, MeshNode> nodes = new(StringComparer.OrdinalIgnoreCase);

    // Note: in prod every silo has its own per-process IStorageAdapter.Changes Subject (fed
    // by PG LISTEN/NOTIFY or the Cosmos change feed). Tests don't bridge those across hosts —
    // consumers that need to observe a specific node bind via
    // workspace.GetMeshNodeStream(path) (same as the GUI), which routes through the owning
    // per-node hub's workspace stream and works without a shared notifier.
    private readonly ConcurrentDictionary<string, List<object>> partitionObjects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Points a host's <see cref="InMemoryStorageAdapter"/> at this cluster's dictionaries.
    ///
    /// <para>What is shared is the STORE, not the change feed: each host still constructs its
    /// own adapter, so each has its own <see cref="IStorageAdapter.Changes"/> Subject. That
    /// mirrors prod, where every silo has a per-process Subject fed by PG LISTEN/NOTIFY —
    /// a cross-silo signal is delivered through <c>IMeshChangeFeed</c>, not through this store.</para>
    /// </summary>
    public IServiceCollection Register(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<InMemoryStorageAdapter>(sp =>
            new InMemoryStorageAdapter(
                nodes,
                partitionObjects,
                sp.GetService<ILoggerFactory>()?.CreateLogger<InMemoryStorageAdapter>())));
        return services;
    }

    /// <summary>
    /// Reads the durable row a test asserts on (what the store of record actually holds).
    /// Read-only by design — a test observes this store, it never mutates or clears it.
    /// </summary>
    public bool TryGetNode(string path, out MeshNode? node) => nodes.TryGetValue(path, out node);
}

/// <summary>
/// A deployed Orleans <see cref="TestCluster"/> together with the Orleans client host that
/// <see cref="OrleansTestCluster.DeployAsync"/> created for it.
///
/// <para><b>Why the client host is ours.</b> Orleans instantiates
/// <see cref="ISiloConfigurator"/> / <see cref="IHostConfigurator"/> types via <c>new()</c>, so
/// a configurator can never be handed an instance — which is why the shared dictionaries used
/// to be statics. For a SILO that no longer matters: its <see cref="InMemoryStorageAdapter"/>
/// is a DI singleton of that silo's own container, i.e. already per-cluster and already dying
/// with the cluster. What was missing was a way to point the CLIENT at that same instance, and
/// <c>TestCluster.InitializeClientAsync</c> offers no hook. <see cref="TestClusterHostFactory.CreateClusterClient"/>
/// does — it takes a post-configure <c>Action&lt;IHostBuilder&gt;</c>, i.e. a CLOSURE — so we
/// deploy the client ourselves with <c>InitializeClientOnDeploy = false</c>.</para>
///
/// <para>Consequence for callers: <c>Cluster.Client</c> is null on these clusters — use
/// <see cref="ClientServices"/> / <see cref="ClusterClient"/> instead.</para>
/// </summary>
public sealed class OrleansTestClusterHost
{
    internal OrleansTestClusterHost(TestCluster cluster, IHost? clientHost)
    {
        Cluster = cluster;
        ClientHost = clientHost;
    }

    public TestCluster Cluster { get; }

    /// <summary>The Orleans client host, or null for silo-only clusters.</summary>
    public IHost? ClientHost { get; }

    public IServiceProvider ClientServices =>
        ClientHost?.Services
        ?? throw new InvalidOperationException(
            "This cluster was deployed without an Orleans client (withClient: false).");

    public IClusterClient ClusterClient => ClientServices.GetRequiredService<IClusterClient>();

    /// <summary>The services of one silo host in this cluster (default: the first).</summary>
    public IServiceProvider SiloServices(int index = 0) => Cluster.SiloServices(index);
}

/// <summary>
/// Deploys Orleans test clusters whose per-cluster state is an INSTANCE rather than a
/// process-wide static. See <see cref="OrleansTestClusterHost"/> for why the client host is
/// created here.
/// </summary>
internal static class OrleansTestCluster
{
    /// <summary>
    /// Builds, deploys and identity-seeds a cluster.
    /// </summary>
    /// <param name="configure">Configures the <see cref="TestClusterBuilder"/> — silo count,
    /// silo/client configurator types.</param>
    /// <param name="configureClientServices">Per-cluster service registrations applied to the
    /// Orleans client host AFTER its configurators have run. The deployed
    /// <see cref="TestCluster"/> is passed in so the client can borrow instances the silos
    /// already own — see <see cref="ShareSiloNodeStore"/>.</param>
    /// <param name="withClient">False for silo-only clusters (no Orleans client host is created).</param>
    public static async Task<OrleansTestClusterHost> DeployAsync(
        Action<TestClusterBuilder> configure,
        Action<IServiceCollection, TestCluster>? configureClientServices = null,
        bool withClient = true)
    {
        var builder = new TestClusterBuilder();
        // We create the client host ourselves (below) so it can receive the per-cluster
        // closure; DeployAsync must not create one first.
        builder.Options.InitializeClientOnDeploy = false;
        // 🚨 And therefore the cluster must speak TCP. TestCluster's default transport is
        // ConnectionTransportType.InMemory, wired through an InMemoryTransportConnectionHub that
        // is a PRIVATE field of TestCluster: DefaultCreateSiloAsync and InitializeClientAsync
        // both hand it to their hosts, and nothing outside can. A client we build ourselves can
        // never join that hub, so it would dial the silos' TCP endpoints and get
        // ConnectionRefused. Declaring TcpSocket makes both ends agree — real loopback sockets,
        // which is what TestCluster used by default before the in-memory transport existed.
        builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        configure(builder);

        var cluster = builder.Build();
        await cluster.DeployAsync();

        IHost? clientHost = null;
        if (withClient)
        {
            clientHost = TestClusterHostFactory.CreateClusterClient(
                "MainClient",
                BuildClientConfiguration(cluster),
                hostBuilder => hostBuilder.ConfigureServices(services =>
                    configureClientServices?.Invoke(services, cluster)));
            await clientHost.StartAsync();
        }

        var host = new OrleansTestClusterHost(cluster, clientHost);
        // DevLogin analog — seed the default System circuit identity on the client + silo
        // mesh hubs so direct test posts satisfy the never-null AccessContext invariant.
        OrleansTestIdentity.SeedDefaultIdentity(host);
        return host;
    }

    /// <summary>
    /// The configuration <c>TestCluster.InitializeClientAsync</c> would have built: the
    /// cluster's own configuration sources PLUS the gateway list, which exists nowhere else.
    /// The gateways are read off the LIVE silos, so this must run after <c>DeployAsync</c>.
    ///
    /// <para>🚨 Without the gateway section the client starts and then fails with
    /// <c>SiloUnavailableException: Could not find any gateway in
    /// 'Orleans.Messaging.StaticGatewayListProvider'</c> — it is not optional, and it is the
    /// one piece of Orleans' client bring-up that owning the client host makes us responsible
    /// for. Kept deliberately faithful to <c>TestCluster.InitializeClientAsync</c>; if Orleans
    /// changes it, every Orleans test fails loudly at deploy rather than subtly at runtime.</para>
    /// </summary>
    private static IConfiguration BuildClientConfiguration(TestCluster cluster)
    {
        var configurationBuilder = new ConfigurationBuilder();
        foreach (var source in cluster.ConfigurationSources)
            configurationBuilder.Add(source);

        if (cluster.Options.UseTestClusterMembership)
        {
            var gateways = cluster.Silos
                .Where(silo => silo.IsActive && silo.GatewayAddress?.Endpoint is { Port: > 0 })
                .Select(silo => new IPEndPoint(
                    silo.GatewayAddress.Endpoint.Address, silo.GatewayAddress.Endpoint.Port))
                .Distinct()
                .ToList();

            if (gateways.Count == 0)
                gateways.AddRange(cluster.Options.GatewayPerSilo
                    ? Enumerable
                        .Range(cluster.Options.BaseGatewayPort, cluster.Options.InitialSilosCount)
                        .Select(port => new IPEndPoint(IPAddress.Loopback, port))
                    : [new IPEndPoint(IPAddress.Loopback, cluster.Options.BaseGatewayPort)]);

            var clustering = new Dictionary<string, string?>();
            var i = 0;
            foreach (var gateway in gateways)
                clustering[$"Orleans:Clustering:Gateways:{i++}"] = gateway.ToString();
            clustering["Orleans:Clustering:ProviderType"] = "Development";
            configurationBuilder.AddInMemoryCollection(clustering);
        }

        return configurationBuilder.Build();
    }

    /// <summary>
    /// Points the Orleans client's <see cref="InMemoryStorageAdapter"/> at the SILO's instance,
    /// so the two hosts are one logical store — the shape production has for free, where every
    /// adapter instance points at the same PostgreSQL database. Without it each DI container
    /// builds its own dictionary and the silo's routing emits NotFound for a path the client
    /// mesh hub just created.
    ///
    /// <para>The silo's adapter is a singleton of that silo's own container: per-cluster by
    /// construction and disposed with it. That is what makes the old process-wide statics
    /// unnecessary rather than merely relocated.</para>
    /// </summary>
    public static void ShareSiloNodeStore(IServiceCollection services, TestCluster cluster) =>
        services.Replace(ServiceDescriptor.Singleton(
            cluster.SiloServices().GetRequiredService<InMemoryStorageAdapter>()));
}

/// <summary>Reaching a silo host's services from a <see cref="TestCluster"/>.</summary>
internal static class OrleansTestClusterExtensions
{
    public static IServiceProvider SiloServices(this TestCluster cluster, int index = 0) =>
        ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;
}
