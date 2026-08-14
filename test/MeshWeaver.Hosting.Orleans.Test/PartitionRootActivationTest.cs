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
/// path — content lives only in satellites (<c>_UserActivity</c>,
/// <c>_Access</c>, …). The path resolver's storage step returns
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
/// to that grain (the user's home address — <c>/rbuergi</c>, <c>/sglauser</c>)
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
    /// Tight budget — pre-fix prod symptom was a 30 s Orleans response timeout
    /// because the grain's <c>_hubReady</c> never completed. Post-fix the
    /// activation chain synthesizes a placeholder MeshNode for the partition
    /// root and the ping responds in &lt; 1 s. 5 s leaves comfortable headroom
    /// for grain startup on a slow CI agent without overlapping the 30 s
    /// deadlock window.
    ///
    /// <para>Measured 2026-08-12 (issue #1289), same machine, five matched arms:
    /// 335 ms alone, 408 ms in a full 156-test suite run, 335 ms under
    /// <c>DOTNET_PROCESSOR_COUNT=2</c>, 352 ms on the previous day's main, 341 ms
    /// in Debug - all while the box carried load average 12.6. So the budget is
    /// ~14x the observed cost, not a thin margin: a run that burns the full 5 s
    /// is a 14x outlier, i.e. the silo stopped answering, and the assertions
    /// below are written to say so.</para>
    /// </summary>
    private static readonly TimeSpan FastBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BarePartitionPath_NoMeshNode_RespondsToPing()
    {
        var client = GetClient($"prtest-{Guid.NewGuid():N}");

        // A bare partition-root path. With InMemoryPartitionStorageProvider
        // (the silo's partition provider, registered via
        // AddPartitionedInMemoryPersistence), every non-empty first segment
        // matches — exactly the prod shape after the hosted-service seed
        // pass registers user partitions. No MeshNode is ever written at the
        // bare path; pre-fix this stranded grain activation.
        var partitionRoot = $"partitionroot-{Guid.NewGuid():N}";

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Ping the grain at the bare partition path. PingRequest is the
        // canonical hub-readiness probe — handled by the default hub config
        // installed via ConfigureDefaultNodeHub (no NodeType required).
        //
        // The budget is spent via .Timeout(), not a CancellationToken, and the
        // TimeoutException is swallowed so the ASSERTIONS below run. With
        // `.ToTask(cancellationToken)` the budget expiring threw a bare
        // TaskCanceledException out of the await, so the two messages that name
        // what a missing ping MEANS (the 30 s prod symptom) never printed and the
        // failure said only "a task was canceled" - a detector that fires without
        // saying what it detected (issue #1289).
        IMessageDelivery<PingResponse>? response = null;
        try
        {
            response = await client
                .Observe(new PingRequest(), o => o.WithTarget(new Address(partitionRoot)))
                .FirstAsync()
                .Timeout(FastBudget)
                .ToTask();
        }
        catch (TimeoutException)
        {
            // Fall through: `response` stays null and the assertion below reports it.
        }

        sw.Stop();

        // Post-fix: ping returns the grain's PingResponse within milliseconds.
        // Pre-fix: no response at all inside FastBudget (and, without the budget,
        // the 30 s Orleans response promise).
        response.Should().NotBeNull(
            "the partition-root fallback must synthesize a MeshNode so MessageHubGrain " +
            "activates — null Node strands activation on Where(r.Node is not null), and " +
            "DeliverMessage burns the 30 s Orleans response budget on every request " +
            "(prod symptom: /rbuergi start screen blank, 30 s 'Response did not arrive on time')");

        sw.Elapsed.Should().BeLessThan(
            FastBudget,
            "ping against a bare-partition root must respond fast — pre-fix this hung " +
            $"the full 30 s grain timeout. Actual: {sw.Elapsed.TotalSeconds:0.0}s.");

        Output.WriteLine(
            $"PASSED — bare partition '{partitionRoot}' activated in {sw.Elapsed.TotalMilliseconds:0}ms");
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
