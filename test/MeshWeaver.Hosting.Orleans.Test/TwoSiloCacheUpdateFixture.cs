#pragma warning disable CS1591

using System;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Two-silo cluster fixture for tests that exercise the cross-silo
/// <c>cache.Update</c> flow. <b>No client</b> — the test driver issues
/// <c>cache.Update</c> directly against a silo's mesh hub; the per-node hub
/// for the target path activates on whichever silo Orleans hashes to.
///
/// <para>Mirrors prod (multiple silos, no separate client process). Replaces
/// the older single-silo+client variant that conflated cross-process
/// <c>IDataChangeNotifier</c> scoping with the cache.Update flow itself.</para>
/// </summary>
public class TwoSiloCacheUpdateFixture : IAsyncLifetime
{
    private OrleansTestClusterHost host = null!;

    public TestCluster Cluster => host.Cluster;

    /// <summary>
    /// Backing store shared by BOTH silos' in-memory adapters, so a Write on one silo is
    /// visible to the other silo's Read (mirrors prod, where every PostgreSQL silo points at
    /// the same DB). An INSTANCE owned by this fixture — it dies with the cluster, so unlike
    /// the process-wide statics it replaced there is nothing to clear before DeployAsync.
    /// </summary>
    private readonly OrleansTestBackingStore backingStore = new();

    /// <summary>Read access to the store of record for tests that assert on durable rows.</summary>
    internal OrleansTestBackingStore BackingStore => backingStore;

    public async ValueTask InitializeAsync()
    {
        // No Orleans client: the test driver issues cache.Update directly against a silo's
        // mesh hub. DeployAsync also seeds the default System circuit identity on both silos'
        // mesh hubs so those direct posts carry an identity (never-null AccessContext
        // invariant).
        host = await OrleansTestCluster.DeployAsync(
            builder =>
            {
                // TWO silos — Orleans hashes the per-node hub grain key onto one of
                // them; cache.Update from the OTHER silo issues a cross-silo grain
                // call. The realistic shape — a single silo can't test the
                // cross-silo routing path.
                builder.Options.InitialSilosCount = 2;
                builder.AddSiloBuilderConfigurator<TwoSiloConfigurator>();

                // 🚨 The ONE fixture that needs a per-cluster instance inside a SILO host.
                // Everywhere else the silo's own DI singleton is already per-cluster and only
                // the client has to be pointed at it; here TWO silos must share ONE store, and
                // neither can be handed an object (Orleans new()s the configurator). Replacing
                // CreateSiloAsync is TestCluster's only silo-host hook. It also replaces
                // DefaultCreateSiloAsync, which is what wires the in-memory connection transport
                // from a hub private to TestCluster — so the setter switches
                // Options.ConnectionTransport to TcpSocket, matching what DeployAsync declares
                // for every cluster whose client we own.
                builder.CreateSiloAsync = async (siloName, configuration) =>
                    await InProcessSiloHandle.CreateAsync(
                        siloName,
                        configuration,
                        hostBuilder => hostBuilder.ConfigureServices(
                            services => backingStore.Register(services)));
            },
            withClient: false);
    }

    public ValueTask DisposeAsync()
    {
        OrleansClusterDisposal.DisposeInBackground(host);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The primary silo's mesh hub. The test driver uses this as its entry
    /// point for <c>cache.Update</c> and <c>workspace.GetMeshNodeStream</c>.
    /// Orleans routes per-node hub grain calls to whichever silo owns the
    /// hash bucket — the test doesn't pick.
    /// </summary>
    public IMessageHub PrimarySiloMeshHub
    {
        get
        {
            var primary = Cluster.Primary;
            var siloHost = primary.GetType().GetProperty("SiloHost")?.GetValue(primary) as IHost
                ?? throw new InvalidOperationException("Could not access primary silo host");
            return siloHost.Services.GetRequiredService<IMessageHub>();
        }
    }
}

internal sealed class TwoSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault()
            .ConfigureLogging(logging => logging.AddXUnitLogger());
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh()
            .AddGraph()
            .AddAI()
            // Both silos' in-memory adapters share the same backing dicts (mirrors prod's
            // shared PG schema) AND the same change-feed Subject (mirrors PG LISTEN/NOTIFY
            // fanout). With the standalone IDataChangeNotifier service removed, every
            // notification flows through IStorageAdapter.Changes; sharing the Subject across
            // silos is the in-memory cluster's equivalent of PG NOTIFY. That store is a
            // per-cluster INSTANCE, so it cannot be registered here (Orleans instantiates this
            // configurator via new()) — TwoSiloCacheUpdateFixture passes it in through
            // OrleansTestCluster.DeployAsync's post-configure closure.
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}
