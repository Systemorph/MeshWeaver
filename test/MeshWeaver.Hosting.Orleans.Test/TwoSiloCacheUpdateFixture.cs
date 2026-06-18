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
    public TestCluster Cluster { get; private set; } = null!;

    /// <summary>
    /// Per-process shared backing dictionaries — both silos' in-memory
    /// adapters share the same store so a Write on one silo is visible to
    /// the other silo's Read (mirrors prod, where every PostgreSQL silo
    /// points at the same DB).
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MeshNode> SharedNodes
        = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<object>> SharedPartitionObjects
        = new(StringComparer.OrdinalIgnoreCase);

    // Note: each silo's IStorageAdapter has its own per-process Changes
    // Subject (mirrors prod). Tests don't bridge them across silos —
    // consumers that need live updates for a specific path use
    // workspace.GetMeshNodeStream(path) (same primitive the GUI binds to),
    // which routes through the owning per-node hub's workspace stream.

    public async ValueTask InitializeAsync()
    {
        // Per-class isolation: these backing dicts are process-wide statics shared
        // across every cluster (Orleans configurators are new()-instantiated, so the
        // static is the only channel both silos' adapters can reach). Reset before
        // DeployAsync so a prior class's leftover nodes don't bleed into this fresh
        // two-silo cluster — same rationale as SharedOrleansFixture.ResetSharedState.
        SharedNodes.Clear();
        SharedPartitionObjects.Clear();

        var builder = new TestClusterBuilder();
        // TWO silos — Orleans hashes the per-node hub grain key onto one of
        // them; cache.Update from the OTHER silo issues a cross-silo grain
        // call. The realistic shape — a single silo can't test the
        // cross-silo routing path.
        builder.Options.InitialSilosCount = 2;
        builder.AddSiloBuilderConfigurator<TwoSiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
        // Seed a default System circuit identity on both silos' mesh hubs so the
        // test driver's direct CreateNodeRequest/cache.Update posts carry an
        // identity (never-null AccessContext invariant). See OrleansTestIdentity.
        OrleansTestIdentity.SeedDefaultIdentity(Cluster);
    }

    public async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            OrleansClusterDisposal.DisposeInBackground(Cluster);
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
            .ConfigureServices(services =>
            {
                // Both silos' in-memory adapters share the same backing dicts
                // (mirrors prod's shared PG schema) AND the same change-feed
                // Subject (mirrors PG LISTEN/NOTIFY fanout). With the
                // standalone IDataChangeNotifier service removed, every
                // notification flows through IStorageAdapter.Changes; sharing
                // the Subject across silos is the in-memory cluster's
                // equivalent of PG NOTIFY.
                services.Replace(ServiceDescriptor.Singleton<InMemoryStorageAdapter>(sp =>
                    new InMemoryStorageAdapter(
                        TwoSiloCacheUpdateFixture.SharedNodes,
                        TwoSiloCacheUpdateFixture.SharedPartitionObjects,
                        sp.GetService<ILoggerFactory>()?.CreateLogger<InMemoryStorageAdapter>())));
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}
