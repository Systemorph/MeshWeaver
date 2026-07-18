#pragma warning disable CS1591

using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// PROBE (issue #325, cross-silo change-feed relay defect 3): does an EXPLICIT per-silo
/// subscriber receive a MeshChangeEvent published from another silo over the shared Orleans
/// memory stream? This is the transport primitive the relay needs — and the question the prior
/// investigation left open (the <c>[ImplicitStreamSubscription]</c> grain never activated).
///
/// <para>Publishes directly through the keyed <see cref="IStreamProvider"/> on silo A, and
/// subscribes directly on silo B (bypassing OrleansMeshChangeFeed / the grain), so a green
/// result isolates "the memory-stream transport CAN carry MeshChangeEvent cross-silo to a
/// non-grain subscriber" from any wiring defect in the feed itself. Bounded everywhere — no
/// Task.Delay, no unbounded await.</para>
/// </summary>
public class OrleansCrossSiloStreamProbeTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture _fixture;

    public OrleansCrossSiloStreamProbeTest(TwoSiloCacheUpdateFixture fixture)
    {
        _fixture = fixture;
    }

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    [Fact(Timeout = 90000)]
    public async Task ExplicitSubscriber_ReceivesCrossSiloPublish()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(70)).Token;
        var cluster = _fixture.Cluster;
        Assert.True(cluster.Silos.Count >= 2, "probe needs two silos");

        var siloA = SiloServices(cluster, 0);
        var siloB = SiloServices(cluster, 1);

        const string streamNs = "mesh-updated";
        var streamId = StreamId.Create(streamNs, Guid.Empty);
        var received = new TaskCompletionSource<MeshChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe on silo B (the NON-publishing silo) via the keyed provider — the exact
        // mechanism OrleansRoutingService uses for cross-silo message delivery.
        var providerB = siloB.GetRequiredKeyedService<IStreamProvider>(StreamProviders.Memory);
        var handle = await providerB.GetStream<MeshChangeEvent>(streamId)
            .SubscribeAsync((evt, _) =>
            {
                received.TrySetResult(evt);
                return Task.CompletedTask;
            })
            .WaitAsync(TimeSpan.FromSeconds(30), ct);

        try
        {
            // Publish from silo A.
            var providerA = siloA.GetRequiredKeyedService<IStreamProvider>(StreamProviders.Memory);
            var evt = new MeshChangeEvent(
                Namespace: "probe",
                Id: "n1",
                Path: "probe/n1",
                Kind: MeshChangeKind.Updated,
                NodeType: "Markdown",
                Version: 1,
                Timestamp: DateTimeOffset.UtcNow);
            await providerA.GetStream<MeshChangeEvent>(streamId).OnNextAsync(evt).WaitAsync(TimeSpan.FromSeconds(30), ct);

            // Assert silo B's subscriber received it (bounded).
            var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.Equal("probe/n1", got.Path);
            Assert.Equal(MeshChangeKind.Updated, got.Kind);
        }
        finally
        {
            try { await handle.UnsubscribeAsync().WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
            catch { /* best-effort teardown */ }
        }
    }
}
