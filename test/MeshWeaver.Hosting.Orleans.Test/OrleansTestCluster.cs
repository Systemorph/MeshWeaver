using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// The in-memory store shared by every host of ONE test cluster — the silo host(s)
/// and the Orleans client host, which live in the same process but build separate DI
/// containers. Production runs PostgreSQL, where several adapter instances point at the
/// same database; the test cluster needs the same shape so a <c>CreateNodeRequest</c>
/// handled on the client mesh hub is visible to the silo's path resolver.
///
/// <para>🚨 This is an INSTANCE, created and owned by the fixture that deploys the
/// cluster, and it dies with it. It replaces the process-wide
/// <c>SharedOrleansFixture.SharedNodes</c> / <c>SharedPartitionObjects</c> statics and the
/// <c>ResetSharedState()</c> that had to wipe them at the start of every cluster init.
/// That wipe was the isolation mechanism, and it is precisely what made the assembly
/// un-parallelisable: class B's init cleared the dictionaries while class A was mid-test,
/// deleting A's nodes ("No node found at …"). With a per-cluster instance there is nothing
/// process-wide to clear, so no reset exists to race. See AGENTS.md → "No static
/// collections" and issue #999.</para>
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
    /// Registered from the host post-configure hook so it runs AFTER the silo/client
    /// configurators have added the default adapter.
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
/// <para>The client host is built here rather than by <c>TestCluster.DeployAsync</c>
/// (<c>InitializeClientOnDeploy = false</c>) because that is what makes per-cluster state
/// possible at all: Orleans instantiates <see cref="ISiloConfigurator"/> /
/// <see cref="IHostConfigurator"/> types via <c>new()</c>, so a configurator can never be
/// handed an instance — which is why the shared dictionaries used to be statics. Both
/// creation paths this class uses (<see cref="InProcessSiloHandle.CreateAsync"/> and
/// <see cref="TestClusterHostFactory.CreateClusterClient"/>) take a post-configure
/// <c>Action&lt;IHostBuilder&gt;</c>, i.e. a CLOSURE, which can capture the per-cluster
/// instances. That closure is the channel the <c>new()</c> constraint denies.</para>
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
}

/// <summary>
/// Deploys Orleans test clusters whose per-cluster state is an INSTANCE rather than a
/// process-wide static. See <see cref="OrleansTestClusterHost"/> for why the client host is
/// created here, and <see cref="OrleansTestBackingStore"/> for what the statics used to be.
/// </summary>
internal static class OrleansTestCluster
{
    /// <summary>
    /// Builds, deploys and identity-seeds a cluster.
    /// </summary>
    /// <param name="configure">Configures the <see cref="TestClusterBuilder"/> — silo count,
    /// silo/client configurator types. Everything Orleans can express as a <c>new()</c>-able
    /// type still goes here.</param>
    /// <param name="configureSiloServices">Per-cluster service registrations applied to every
    /// silo host AFTER its configurators have run. This is where instance state (a
    /// <see cref="OrleansTestBackingStore"/>, a chat-client factory) enters the silo container.</param>
    /// <param name="configureClientServices">The same, for the Orleans client host.</param>
    /// <param name="withClient">False for silo-only clusters (no Orleans client host is created).</param>
    public static async Task<OrleansTestClusterHost> DeployAsync(
        Action<TestClusterBuilder> configure,
        Action<IServiceCollection>? configureSiloServices = null,
        Action<IServiceCollection>? configureClientServices = null,
        bool withClient = true)
    {
        var builder = new TestClusterBuilder();
        // We create the client host ourselves (below) so it can receive the per-cluster
        // closure; DeployAsync must not create one first.
        builder.Options.InitializeClientOnDeploy = false;
        configure(builder);

        if (configureSiloServices is not null)
            builder.CreateSiloAsync = async (siloName, configuration) =>
                await InProcessSiloHandle.CreateAsync(
                    siloName,
                    configuration,
                    hostBuilder => hostBuilder.ConfigureServices(configureSiloServices));

        var cluster = builder.Build();
        await cluster.DeployAsync();

        IHost? clientHost = null;
        if (withClient)
        {
            // Mirrors TestCluster.InitializeClientAsync — same configuration sources, so the
            // client resolves the same gateway list and the same ClientBuilderConfigurator
            // types — plus the post-configure closure Orleans' own path has no room for.
            var configurationBuilder = new ConfigurationBuilder();
            foreach (var source in cluster.ConfigurationSources)
                configurationBuilder.Add(source);

            clientHost = TestClusterHostFactory.CreateClusterClient(
                "MainClient",
                configurationBuilder.Build(),
                hostBuilder =>
                {
                    if (configureClientServices is not null)
                        hostBuilder.ConfigureServices(configureClientServices);
                });
            await clientHost.StartAsync();
        }

        var host = new OrleansTestClusterHost(cluster, clientHost);
        // DevLogin analog — seed the default System circuit identity on the client + silo
        // mesh hubs so direct test posts satisfy the never-null AccessContext invariant.
        OrleansTestIdentity.SeedDefaultIdentity(host);
        return host;
    }
}
