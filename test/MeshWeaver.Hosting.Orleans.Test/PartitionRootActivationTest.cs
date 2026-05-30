using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Real Orleans regression for the prod "no view on rbuergi" outage.
///
/// <para>Scenario: a user (or org) partition is registered with the
/// <see cref="MeshWeaver.Mesh.Services.IPartitionStorageProvider"/> but has
/// NO MeshNode in the primary <c>mesh_nodes</c> table at the bare partition
/// path â€” content lives only in satellites (<c>_UserActivity</c>,
/// <c>_Access</c>, â€¦). The path resolver's storage step returns
/// <c>(null, 0)</c>; only the partition-root fallback can produce a
/// resolution for the bare path.</para>
///
/// <para>Before the fix, that fallback returned
/// <see cref="MeshWeaver.Mesh.Services.AddressResolution"/> with
/// <c>Node = null</c>.
/// <see cref="MeshWeaver.Hosting.Orleans.MessageHubGrain.OnActivateAsync"/>
/// subscribes with <c>Where(r =&gt; r.Node is not null)</c>, so the null-Node
/// resolution was filtered out, the source observable never emitted,
/// <c>_hubReady</c> stayed pending forever, and every <c>DeliverMessage</c>
/// to that grain (the user's home address â€” <c>/rbuergi</c>, <c>/sglauser</c>)
/// timed out at exactly 30 s with "Response did not arrive on time".</para>
///
/// <para>This test pings a bare partition path with no MeshNode and asserts
/// the response arrives well inside the 30 s Orleans grain budget. Pre-fix:
/// times out at ~30 s. Post-fix: completes in &lt; 5 s.</para>
/// </summary>
public class PartitionRootActivationTest(ITestOutputHelper output)
    : OrleansTestBase<PartitionRootSiloConfigurator>(output)
{
    /// <summary>
    /// Tight budget â€” pre-fix prod symptom was a 30 s Orleans response timeout
    /// because the grain's <c>_hubReady</c> never completed. Post-fix the
    /// activation chain synthesizes a placeholder MeshNode for the partition
    /// root and the ping responds in &lt; 1 s. 5 s leaves comfortable headroom
    /// for grain startup on a slow CI agent without overlapping the 30 s
    /// deadlock window.
    /// </summary>
    private static readonly TimeSpan FastBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BarePartitionPath_NoMeshNode_RespondsToPing()
    {
        var ct = new CancellationTokenSource(FastBudget).Token;
        var client = await GetClientAsync($"prtest-{Guid.NewGuid():N}");

        // A bare partition-root path. With InMemoryPartitionStorageProvider
        // (the silo's partition provider, registered via
        // AddPartitionedInMemoryPersistence), every non-empty first segment
        // matches â€” exactly the prod shape after the hosted-service seed
        // pass registers user partitions. No MeshNode is ever written at the
        // bare path; pre-fix this stranded grain activation.
        var partitionRoot = $"partitionroot-{Guid.NewGuid():N}";

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Ping the grain at the bare partition path. PingRequest is the
        // canonical hub-readiness probe â€” handled by the default hub config
        // installed via ConfigureDefaultNodeHub (no NodeType required).
        var response = await client
            .Observe(new PingRequest(), o => o.WithTarget(new Address(partitionRoot)))
            .FirstAsync()
            .ToTask(ct);

        sw.Stop();

        // Post-fix: ping returns the grain's PingResponse within milliseconds.
        // Pre-fix: would time out at the FastBudget cancellation token
        // (CancellationException) or at the 30 s Orleans response promise.
        response.Should().NotBeNull(
            "the partition-root fallback must synthesize a MeshNode so MessageHubGrain " +
            "activates â€” null Node strands activation on Where(r.Node is not null), and " +
            "DeliverMessage burns the 30 s Orleans response budget on every request " +
            "(prod symptom: /rbuergi start screen blank, 30 s 'Response did not arrive on time')");

        sw.Elapsed.Should().BeLessThan(
            FastBudget,
            "ping against a bare-partition root must respond fast â€” pre-fix this hung " +
            $"the full 30 s grain timeout. Actual: {sw.Elapsed.TotalSeconds:0.0}s.");

        Output.WriteLine(
            $"PASSED â€” bare partition '{partitionRoot}' activated in {sw.Elapsed.TotalMilliseconds:0}ms");
    }
}

/// <summary>
/// Silo configurator: partitioned in-memory persistence + default node hub.
/// InMemoryPartitionStorageProvider matches any non-empty first segment,
/// mirroring how a Postgres-backed prod silo handles per-user partitions
/// after the hosted-service schema-discovery pass.
/// </summary>
public class PartitionRootSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddPartitionedInMemoryPersistence()
            .ConfigurePortalMesh();
    }
}
