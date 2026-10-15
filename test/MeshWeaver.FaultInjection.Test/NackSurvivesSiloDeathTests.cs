using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Streams;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>#4802 slice 4 — a NACK reaches a sender on another silo with no memory stream anywhere, and a
/// silo dying while the NACK is in flight does not lose it.</b>
///
/// <para>Before: when the router could not reach a sender it did not host over the directed call, the
/// <c>DeliveryFailure</c> fell back to the sender's Orleans memory stream — a publish that succeeds
/// when nobody is subscribed, into a <c>MemoryStreamQueueGrain</c> held in the RAM of ONE arbitrary
/// silo. When that silo left (every rolling deploy), the NACK went with it and the sender waited out
/// its whole budget. Now a multi-silo host registers no memory stream at all, and the NACK is routed
/// like any other message to its sender: the directed grain call to the sender's host
/// (<c>RoutingGrain.PostFailure</c>), with the forward leg's transient retry.</para>
///
/// <para><b>The fault, deterministically.</b> Three silos configured as a MULTI-SILO host (a durable
/// PubSubStore — so, since #4802, no memory stream; the case asserts that first). A pod-process hub
/// is registered on silo 0 and its claim awaited. Silo 1's router is handed a request from that hub
/// to an address no node serves, so it NACKs a sender it does not host. Every NACK leaving silo 1 is
/// HELD; the hold's arrival proves the NACK is in flight — and silo 2 is then KILLED (no graceful
/// stop). Then the hold lets go.</para>
///
/// <list type="bullet">
///   <item><b>Fixed:</b> the in-flight NACK is the router's own directed call, so the death of a
///     third silo cannot touch it: the sender on silo 0 hears its <c>DeliveryFailure</c>.</item>
///   <item><b>Negative control:</b> the identical sequence, but the held NACK lives in a carrier that
///     dies WITH silo 2 — the memory stream's shape, where the published NACK sat in a queue grain
///     on whichever silo hosted it. The kill discards it (<see cref="NackHold.CarrierDied"/>) and the
///     sender never hears it. This is what proves the fixed arm's arrival is the held NACK and
///     nothing else: no stream, no second publish, no timer.</item>
/// </list>
/// </summary>
public abstract class ANackInFlightWhenASiloDies(FaultInjectionCluster mesh)
{
    private const int Owner = 0;
    private const int Router = 1;
    private const int Bystander = 2;

    /// <summary>Whether the in-flight NACK lives in the router's own call (fixed) or in a carrier that dies with the bystander.</summary>
    protected abstract bool CarrierOutlivesTheDeadSilo { get; }

    [Fact(Timeout = 120_000)]
    public async Task TheNack_ReachesTheSenderOnAnotherSilo_WithNoMemoryStream()
    {
        var ct = TestContext.Current.CancellationToken;

        // ── Precondition: the multi-silo shape has NO memory stream to fall back on ─────────────────
        for (var i = 0; i < 3; i++)
            mesh.Silo(i).GetKeyedService<IStreamProvider>(StreamProviders.Memory).Should().BeNull(
                $"silo {i} is configured as a multi-silo host, which registers no memory stream (#4802) — "
                + "with one present this case could be carried by the stream and would prove nothing");
        var ownerRouting = mesh.Hub(Owner).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        ownerRouting.HasMemoryStream.Should().BeFalse("the routing service reads the same registration");

        // ── The sender: a pod-process hub on silo 0, claimed through its IPodHubGrain ───────────────
        var sender = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"nack-{Guid.NewGuid():N}");
        var heard = new ReplaySubject<IMessageDelivery>();
        using var registration = ownerRouting.RegisterStream(sender, (d, _) =>
        {
            heard.OnNext(d);
            return Observable.Return(d.Processed());
        });
        await (ownerRouting.PodHubClaimSettled(sender) ?? Observable.Return(Unit.Default))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // ── The fault: the NACK is in flight when a silo dies ───────────────────────────────────────
        var hold = mesh.Silo(Router).GetRequiredService<NackHold>();
        using var inFlight = hold.Hold();
        var request = new MessageDelivery<PingRequest>(
            new PingRequest(),
            new PostOptions(sender).WithTarget(new Address("nack-missing", Guid.NewGuid().ToString("N"))),
            System.Text.Json.JsonSerializerOptions.Default);
        await mesh.Silo(Router).GetRequiredService<IGrainFactory>().GetGrain<IRoutingGrain>("default")
            .RouteMessage(request).WaitAsync(TestTimeouts.Convergence, ct);
        await inFlight.Arrivals.Where(a => a.Contains(request.Id, StringComparison.Ordinal))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("silo 1's router NACKs the sender it does not host — otherwise this case injected nothing", ct);

        await mesh.Kill(Bystander);
        if (!CarrierOutlivesTheDeadSilo)
            hold.CarrierDied();
        inFlight.Release();

        var nack = heard.Where(d => d.Properties.TryGetValue(PostOptions.RequestId, out var id)
                                    && string.Equals(id?.ToString(), request.Id, StringComparison.Ordinal));
        if (CarrierOutlivesTheDeadSilo)
            await nack.Should().Within(TestTimeouts.Convergence)
                .Emit("the NACK is the router's own directed call to the sender's silo, so a third silo "
                      + "dying while it is in flight cannot lose it (#4802)", ct);
        else
            await nack.Should().NotEmit(TimeSpan.FromSeconds(5),
                "with a carrier that dies with silo 2, nothing else carries the NACK — if this arm ever "
                + "hears it, something other than the held directed call is delivering and the fixed arm "
                + "proves nothing", ct);
    }
}

/// <summary>The fix: the NACK's carrier is the router's directed call, which a third silo's death does not touch.</summary>
public class ANackReachesTheSenderWhenASiloDiesMidFlightTest(ANackReachesTheSenderWhenASiloDiesMidFlightTest.Cluster mesh)
    : ANackInFlightWhenASiloDies(mesh), IClassFixture<ANackReachesTheSenderWhenASiloDiesMidFlightTest.Cluster>
{
    protected override bool CarrierOutlivesTheDeadSilo => true;

    /// <summary>Three multi-silo silos: the sender's owner, the router, a bystander.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 3;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloNackHoldConfigurator);
    }
}

/// <summary>Negative control: a NACK whose carrier dies with the killed silo is lost.</summary>
public class ANackIsLostWhenItsCarrierDiesWithTheSiloTest(ANackIsLostWhenItsCarrierDiesWithTheSiloTest.Cluster mesh)
    : ANackInFlightWhenASiloDies(mesh), IClassFixture<ANackIsLostWhenItsCarrierDiesWithTheSiloTest.Cluster>
{
    protected override bool CarrierOutlivesTheDeadSilo => false;

    /// <summary>Three multi-silo silos: the sender's owner, the router, a bystander.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 3;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloNackHoldConfigurator);
    }
}

/// <summary>
/// Every silo configured as a MULTI-SILO host (a durable PubSubStore marker — so no memory stream),
/// plus the outgoing NACK hold.
/// </summary>
public class MultiSiloNackHoldConfigurator : FaultInjectionSiloConfigurator
{
    /// <inheritdoc />
    protected override Action<ISiloBuilder>? PubSubStore =>
        silo => silo.AddMemoryGrainStorage(StreamProviders.PubSubStore);

    /// <inheritdoc />
    public override void Configure(ISiloBuilder siloBuilder)
    {
        base.Configure(siloBuilder);
        siloBuilder.Services.AddSingleton<NackHold>();
        siloBuilder.AddOutgoingGrainCallFilter<NackHoldFilter>();
    }
}

/// <summary>
/// The hold on NACKs LEAVING one silo: a <see cref="DeliveryFailure"/> sent over a directed grain call
/// waits until the switch is released. One instance per silo (a DI singleton), never static.
/// </summary>
public sealed class NackHold
{
    private FaultSwitch? _hold;
    private int _carrierDied;

    /// <summary>Holds every NACK leaving this silo until the returned switch is released.</summary>
    public FaultSwitch Hold()
    {
        Interlocked.Exchange(ref _carrierDied, 0);
        var fault = new FaultSwitch("hold NACKs leaving this silo");
        Volatile.Write(ref _hold, fault);
        return fault;
    }

    /// <summary>
    /// The carrier holding the NACK died (the negative control's model of a RAM queue grain on the
    /// killed silo): on release the held NACK is discarded instead of sent.
    /// </summary>
    public void CarrierDied() => Interlocked.Exchange(ref _carrierDied, 1);

    internal FaultSwitch? Current => Volatile.Read(ref _hold);

    internal bool Died => Volatile.Read(ref _carrierDied) != 0;
}

/// <summary>Applies <see cref="NackHold"/> to outgoing grain calls that carry a NACK.</summary>
internal sealed class NackHoldFilter(NackHold hold) : IOutgoingGrainCallFilter
{
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (hold.Current is { IsClosed: true } fault
            && context.Request.GetArgumentCount() == 1
            && context.Request.GetArgument(0) is IMessageDelivery { Message: DeliveryFailure } nack)
        {
            var requestId = nack.Properties.TryGetValue(PostOptions.RequestId, out var id) ? id?.ToString() : null;
            await fault.Gate(Observable.Return(Unit.Default),
                    $"NACK for {requestId} → {nack.Target} over {context.InterfaceName}.{context.MethodName}")
                .Await();
            if (hold.Died)
                throw new InvalidOperationException(
                    $"The carrier holding the NACK for {requestId} died with its silo; the NACK is lost.");
        }
        await context.Invoke();
    }
}
