using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the #1129 root fix: <c>OrleansRoutingService.RegisterStream</c> must NOT touch the
/// Orleans stream provider before the Orleans lifecycle reports streaming usable
/// (<see cref="OrleansStreamingReadiness"/>, completed at <c>ServiceLifecycleStage.Active</c>).
/// <c>GetStream</c> on a <c>PersistentStreamProvider</c> whose lifecycle Init has not run yet
/// NREs from deep inside Orleans — the eagerly-created cache/mesh hubs lost exactly that race
/// on every memex pod boot (2 Error-level NREs per boot), papered over by a poll-retry loop.
/// Deterministic unit test, no cluster: the stream provider is a recording probe — REACHING it
/// is the observable, so both directions of the gate are assertable.
/// </summary>
public class OrleansStreamingReadinessGateTest
{
    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private int getStreamCalls;
        public int GetStreamCalls => Volatile.Read(ref getStreamCalls);
        public string Name => StreamProviders.Memory;
        public bool IsRewindable => false;

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            Interlocked.Increment(ref getStreamCalls);
            // Any touch BEFORE readiness would have been the #1129 NRE; the gate must make this
            // unreachable until the lifecycle observer has run. The throw keeps the fake minimal —
            // the attach-success path is covered by the real-cluster routing tests.
            throw new NotSupportedException("attach probe — reaching the provider is the assertion");
        }
    }

    [Fact]
    public async Task RegisterStream_TouchesStreamProvider_OnlyAfterLifecycleReportsReady()
    {
        var provider = new RecordingStreamProvider();
        var readiness = new OrleansStreamingReadiness();
        var services = new ServiceCollection();
        services.AddSingleton(readiness);
        services.AddKeyedSingleton<IStreamProvider>(StreamProviders.Memory, provider);
        await using var sp = services.BuildServiceProvider();

        // grainFactory is deliberately null — RegisterStream never places grains, so reaching it
        // would throw and fail the test (the same probe technique as the shutdown-classification test).
        using var routing = new OrleansRoutingService(
            null!,
            sp,
            NullLogger<OrleansRoutingService>.Instance);

        using var registration = routing.RegisterStream(
            AddressExtensions.CreateMeshAddress("streaming-gate-test"),
            (d, _) => Observable.Return(d));

        // Negative control: the gate must HOLD while the lifecycle has not reported ready.
        // (Sanctioned "confirm nothing happened" wait — there is no positive signal to filter for.)
        await Task.Delay(500);
        provider.GetStreamCalls.Should().Be(0,
            "the stream provider must never be touched before the Orleans lifecycle reaches Active — " +
            "that touch is the #1129 NRE");

        // Open the gate exactly the way Orleans does: the lifecycle observer's OnStart at Active.
        await ((ILifecycleObserver)readiness).OnStart(CancellationToken.None);

        // The attach must now reach the provider (poll the recorded call, never a fixed sleep).
        var calls = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => provider.GetStreamCalls)
            .Where(c => c > 0)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .ToTask();
        calls.Should().Be(1, "after readiness the subscription attaches exactly once — no retry loop");
    }
}
