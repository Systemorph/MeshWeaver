using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

// TODO: needs custom shared fixture — depends on TestSiloConfigurator's AddFileSystemAssemblyStore,
// which the SharedOrleansFixture does not configure.
/// <summary>
/// Proves the distributed-cache invariant in an actual Orleans cluster: the
/// content-addressed <see cref="IAssemblyStore"/> is registered on every silo, and a
/// Put on silo A is observable as a TryGet hit on silo B. This is the core guarantee
/// that lets us replace the per-replica in-memory compile cache without losing
/// cross-replica consistency — which is why "status=Ok" on one replica now means the
/// same thing on every other replica (same hash → same blob → same bytes).
/// </summary>
public class OrleansAssemblyStoreTest(ITestOutputHelper output) : OrleansTestBase(output)
{
    /// <summary>
    /// Two silos so the Put-on-A / TryGet-on-B invariant can actually be exercised —
    /// the base default is 1, which would fail the <c>silos.Count &gt;= 2</c> assertion.
    /// </summary>
    protected override short InitialSilosCount => 2;

    [Fact(Timeout = 30000)]
    public async Task Put_on_one_silo_is_visible_as_TryGet_hit_on_another()
    {
        // Grab the assembly store from two distinct silos in the cluster. Orleans
        // TestCluster spins up 2 silos by default (Primary + Secondary).
        var silos = Cluster.Silos;
        silos.Count.Should().BeGreaterThanOrEqualTo(2, "test needs two silos to verify sharing");

        var siloA = ((InProcessSiloHandle)silos[0]).SiloHost.Services.GetRequiredService<IAssemblyStore>();
        var siloB = ((InProcessSiloHandle)silos[1]).SiloHost.Services.GetRequiredService<IAssemblyStore>();
        siloA.Should().NotBeSameAs(siloB, "each silo has its own singleton instance");

        // Make the test hermetic by writing to a path + version unlikely to collide with other runs.
        var nodeTypePath = "OrleansAssemblyStoreTest/Shared";
        long version = System.DateTime.UtcNow.Ticks; // monotonic, unique per run

        var bytes = Encoding.UTF8.GetBytes("compiled-on-silo-0");

        // Put on silo A — Observable, wait for the single emission.
        var putPath = await siloA.Put(nodeTypePath, version, bytes, pdbBytes: null)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        File.Exists(putPath).Should().BeTrue();

        // TryGet on silo B — must see the same file thanks to the shared root.
        var getPath = await siloB.TryGetAssemblyPath(nodeTypePath, version)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        getPath.Should().NotBeNull("silo B must observe silo A's write via shared storage");
        File.ReadAllBytes(getPath!).Should().BeEquivalentTo(bytes);
    }

    [Fact(Timeout = 30000)]
    public async Task TryGet_on_unknown_version_emits_null_across_the_cluster()
    {
        var silos = Cluster.Silos;
        var siloA = ((InProcessSiloHandle)silos[0]).SiloHost.Services.GetRequiredService<IAssemblyStore>();

        var path = await siloA.TryGetAssemblyPath("Never/Compiled", version: 999999999L)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);
        path.Should().BeNull();
    }
}
