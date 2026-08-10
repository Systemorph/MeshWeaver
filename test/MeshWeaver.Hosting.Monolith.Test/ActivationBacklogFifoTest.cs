using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>Probe request whose arrival order at the target hub the test asserts on.</summary>
public record OrderProbeRequest(string Tag) : IRequest<OrderProbeResponse>;

/// <summary>Response for <see cref="OrderProbeRequest"/> so the test can await completion.</summary>
public record OrderProbeResponse(string Tag);

/// <summary>
/// Mesh-scoped instance recorder (never static — NoStaticState.md) capturing the
/// order in which the probe hub's handler processed the deliveries.
/// </summary>
public sealed class DeliveryOrderRecorder
{
    private ImmutableList<string> tags = ImmutableList<string>.Empty;

    /// <summary>Appends one processed tag.</summary>
    public void Record(string tag) => ImmutableInterlocked.Update(ref tags, static (l, t) => l.Add(t), tag);

    /// <summary>The tags in processing order.</summary>
    public ImmutableList<string> Snapshot => tags;
}

/// <summary>
/// Deterministic repro for issue #1145 (three back-to-back kernel submissions lose shared
/// state — CS0103 on the monolith path).
///
/// <para><b>The hole:</b> <c>RoutingServiceBase.RouteInMesh</c> short-circuits to
/// <c>Mesh.GetHostedHub(address, Never)</c> the moment the target hub is REGISTERED — but a hub
/// becomes registered mid-activation (<c>HostedHubsCollection.GetHub</c> publishes it to
/// <c>messageHubs</c> the instant construction completes), while earlier messages for the same
/// address are still queued in the per-address <c>ActivationSerializer</c>. A message arriving in
/// that window takes the direct path and OVERTAKES every delivery still in the backlog. For the
/// kernel that means submission #2 ("use sharedValue") reaches the REPL before submission #1
/// ("define sharedValue") → CS0103.</para>
///
/// <para><b>The script:</b> a gated <see cref="IPathResolver"/> decorator parks the FIRST
/// resolution of the probe path (message A activating the hub — serializer alive, backlog [A])
/// and the SECOND (message B's serializer turn — which only starts after A's activation
/// registered the hub, so "hub registered + backlog undrained" holds deterministically). C is
/// posted in exactly that state. The contract: the probe hub must process A, B, C in post
/// order. Before the fix C leapfrogs B deterministically (order A, C, B).</para>
/// </summary>
public class ActivationBacklogFifoTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ProbeId = "order-probe";
    private static string ProbePath => $"{TestPartition}/{ProbeId}";

    private readonly ResolutionGates gates = new();

    /// <summary>
    /// Per-mesh gate controller for the decorated path resolver. Instance state on the
    /// test class (one mesh per test) — never static.
    /// </summary>
    private sealed class ResolutionGates
    {
        private int probeCalls;
        private readonly ReplaySubject<int> calls = new();
        private readonly ReplaySubject<Unit> first = new();
        private readonly ReplaySubject<Unit> second = new();

        /// <summary>Emits 1, 2, 3… as probe-path resolutions are requested (replayed).</summary>
        public IObservable<int> Calls => calls;

        /// <summary>Releases the first probe-path resolution (message A's activation).</summary>
        public void ReleaseFirst() => first.OnNext(Unit.Default);

        /// <summary>Releases the second probe-path resolution (message B's serializer turn).</summary>
        public void ReleaseSecond() => second.OnNext(Unit.Default);

        /// <summary>Gate observable for one resolution request of <paramref name="path"/>.</summary>
        public IObservable<Unit> Gate(string path)
        {
            if (!path.EndsWith(ProbeId, StringComparison.Ordinal))
                return Observable.Return(Unit.Default);
            var call = Interlocked.Increment(ref probeCalls);
            calls.OnNext(call);
            return call switch
            {
                1 => first.Take(1),
                2 => second.Take(1),
                _ => Observable.Return(Unit.Default)
            };
        }
    }

    /// <summary>
    /// Timing-control decorator over the REAL <see cref="PathResolutionService"/> — no data is
    /// faked; only WHEN a resolution emits is controlled, exactly the CI-load jitter that makes
    /// #1145 intermittent, made deterministic.
    /// </summary>
    private sealed class GatedPathResolver(IPathResolver inner, ResolutionGates gates) : IPathResolver
    {
        public IObservable<AddressResolution?> ResolvePath(string path)
            => gates.Gate(path).SelectMany(_ => inner.ResolvePath(path));

        public IObservable<AddressResolution?> ResolveNavigationPath(string path)
            => inner.ResolveNavigationPath(path);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(MeshNode.FromPath(ProbePath) with
            {
                Name = "Order Probe",
                HubConfiguration = config => config
                    .WithType(typeof(OrderProbeRequest), nameof(OrderProbeRequest))
                    .WithType(typeof(OrderProbeResponse), nameof(OrderProbeResponse))
                    .WithHandler<OrderProbeRequest>((hub, delivery) =>
                    {
                        hub.ServiceProvider.GetRequiredService<DeliveryOrderRecorder>()
                            .Record(delivery.Message.Tag);
                        hub.Post(new OrderProbeResponse(delivery.Message.Tag), o => o.ResponseFor(delivery));
                        return delivery.Processed();
                    })
            })
            .ConfigureServices(s => s
                .AddSingleton<DeliveryOrderRecorder>()
                // Last registration wins for single resolution; the framework's
                // TryAddSingleton<IPathResolver> cannot override an existing one either
                // way, so this decorator is what RoutingServiceBase resolves.
                .AddSingleton<IPathResolver>(sp =>
                    new GatedPathResolver(sp.GetRequiredService<PathResolutionService>(), gates)));

    [Fact(Timeout = 60_000)]
    public async Task MessageArrivingWhileActivationBacklogDrains_MustNotLeapfrogIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var probeAddress = new Address(ProbePath);
        var client = GetClient(c => ConfigureClient(c)
            .WithType(typeof(OrderProbeRequest), nameof(OrderProbeRequest))
            .WithType(typeof(OrderProbeResponse), nameof(OrderProbeResponse)));

        // Pre-activate the fence hub (the TestData partition node — NOT the mesh hub,
        // which is the router and flags direct work as ROUTER_TRAFFIC). Later fence
        // pings to it ride the same client→mesh mailbox as the probe messages, so a
        // fence response proves the mesh has processed everything posted before it.
        var fenceAddress = new Address(TestPartition);
        await client.Observe(new PingRequest(), o => o.WithTarget(fenceAddress))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);

        // A — first message to the not-yet-activated probe hub. Its activation parks at
        // resolver call #1: the per-address ActivationSerializer is now alive with A pending.
        var aTask = client.Observe<OrderProbeResponse>(new OrderProbeRequest("A"),
                o => o.WithTarget(probeAddress))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);
        await gates.Calls.Where(c => c == 1).FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);
        Output.WriteLine("A parked in activation (resolver call #1).");

        // B — joins the activation backlog behind A. Fence: a ping to the pre-activated
        // fence hub rides the same client→mesh mailbox, so its response proves the mesh
        // has processed B's RouteInMesh (FIFO per mailbox) — B is provably enqueued
        // before A is released.
        var bTask = client.Observe<OrderProbeResponse>(new OrderProbeRequest("B"),
                o => o.WithTarget(probeAddress))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);
        await client.Observe(new PingRequest(), o => o.WithTarget(fenceAddress))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);
        Output.WriteLine("B enqueued behind A (mesh fence passed).");

        // Release A: the hub is constructed + REGISTERED, A is delivered, and B's serializer
        // turn starts — parking at resolver call #2. Call #2 firing therefore proves the
        // leapfrog window is open: hub registered, backlog (B) undrained.
        gates.ReleaseFirst();
        await gates.Calls.Where(c => c == 2).FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);
        Mesh.GetHostedHub(probeAddress, HostedHubCreation.Never).Should().NotBeNull(
            "A's activation must have registered the probe hub before B's serializer turn");
        Output.WriteLine("Hub registered; B parked in serializer turn (resolver call #2).");

        // C — posted exactly in the window. It must NOT bypass the still-draining backlog.
        var cTask = client.Observe<OrderProbeResponse>(new OrderProbeRequest("C"),
                o => o.WithTarget(probeAddress))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);
        await client.Observe(new PingRequest(), o => o.WithTarget(fenceAddress))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask(ct);
        Output.WriteLine("C routed (mesh fence passed).");

        // Release B; every message must now complete.
        gates.ReleaseSecond();
        await Task.WhenAll(aTask, bTask, cTask);

        var recorder = Mesh.ServiceProvider.GetRequiredService<DeliveryOrderRecorder>();
        Output.WriteLine($"Processing order: {string.Join(", ", recorder.Snapshot)}");
        recorder.Snapshot.Should().Equal(new[] { "A", "B", "C" },
            "messages to one address must be processed in post order even when a later message "
            + "arrives while the hub is already registered but the activation backlog has not "
            + "drained — the #1145 kernel CS0103 reorder");
    }
}
