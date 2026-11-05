using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
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
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>#7888: a delivery that reaches a pod hub between its disposal and its release is answered
/// RELEASED, not "claim stranded".</b>
///
/// <para>Disposing a registration removes its local route synchronously and then releases the
/// pod-hub claim with a fire-and-forget <c>IPodHubGrain.Detach</c>. A delivery reaching the owner's
/// activation in between found no route and no Released tombstone, so it got the TRANSIENT refusal.
/// That refusal is the shape that means "a live hub's claim is stranded" (#2938), which the router
/// must report at Error. Measured on memex-cloud: a closed circuit's hub was disposed at
/// 14:38:45.497Z, the NotFound reply to its last message was refused this way at .514, and the router
/// logged an undeliverable NACK at Error for a sender nobody was waiting on.</para>
///
/// <para><b>The gap, deterministically.</b> An outgoing grain-call filter on the owner's silo HOLDS
/// that address's <c>Detach</c>. The hold's arrival proves the registration has been disposed and its
/// release is in flight. A delivery is then issued to the owner's activation, the release is let go,
/// and a second delivery confirms the tombstone.</para>
/// <list type="bullet">
///   <item><b>Fixed:</b> the delivery in the gap is refused with <c>Released = true</c>, which
///     <c>RoutingGrain.AnswerNackToPodHubSenderRefused</c> drops at Debug.</item>
///   <item><b>Without the fix</b> (measured locally, see the PR): the same delivery is refused with
///     <c>Released = false</c>.</item>
///   <item><b>Negative control:</b> an address that no registration in this process is releasing (the
///     stranded-claim shape) still gets the transient refusal, so the loud path is not weakened.</item>
/// </list>
/// </summary>
public class AReplyInTheReleaseGapIsAnsweredReleasedTest(AReplyInTheReleaseGapIsAnsweredReleasedTest.Cluster mesh)
    : IClassFixture<AReplyInTheReleaseGapIsAnsweredReleasedTest.Cluster>
{
    private const int Owner = 0;

    [Fact(Timeout = 120_000)]
    public async Task ADeliveryBetweenDisposalAndRelease_IsRefusedReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var routing = mesh.Hub(Owner).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        var address = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"closing-{Guid.NewGuid():N}");

        var registration = routing.RegisterStream(address, (d, _) => Observable.Return(d.Processed()));
        await (routing.PodHubClaimSettled(address) ?? Observable.Return(Unit.Default))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var hold = mesh.Silo(Owner).GetRequiredService<ReleaseHold>();
        using var release = hold.Hold(address.ToString());
        registration.Dispose();
        await release.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the disposal's Detach must be in flight and held, or this case injected nothing", ct);

        routing.TryGetLocalRoute(address).Should().BeNull("PRECONDITION: the disposal removed the route");
        var grain = mesh.Silo(Owner).GetRequiredService<IGrainFactory>().GetGrain<IPodHubGrain>(address.ToString());

        var inTheGap = await Assert.ThrowsAsync<PodHubNotHereException>(() =>
            grain.Deliver(Delivery(address, "reply-in-the-gap")).WaitAsync(TestTimeouts.Convergence, ct));
        inTheGap.Released.Should().BeTrue(
            "the owner on this silo disposed its registration and its release is in flight: the address is "
            + "gone for good, so the transient 'claim stranded' refusal (an Error-level NACK at the router) is "
            + "false here (#7888)");

        release.Release();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => routing.IsReleaseInFlight(address))
            .Where(inFlight => !inFlight)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the marker is cleared once the Detach has returned — it is bounded by one release call", ct);

        var afterTheRelease = await Assert.ThrowsAsync<PodHubNotHereException>(() =>
            grain.Deliver(Delivery(address, "after-the-release")).WaitAsync(TestTimeouts.Convergence, ct));
        afterTheRelease.Released.Should().BeTrue("the Detach landed and stamped the tombstone");
    }

    /// <summary>
    /// NEGATIVE CONTROL: no registration in this process is releasing the address, so a delivery that
    /// finds no route still gets the TRANSIENT refusal. That is the stranded-claim shape the router must
    /// keep reporting at Error.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAddressNobodyIsReleasing_StillGetsTheTransientRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"unclaimed-{Guid.NewGuid():N}");
        var grain = mesh.Silo(Owner).GetRequiredService<IGrainFactory>().GetGrain<IPodHubGrain>(address.ToString());

        var refusal = await Assert.ThrowsAsync<PodHubNotHereException>(() =>
            grain.Deliver(Delivery(address, "to-nobody")).WaitAsync(TestTimeouts.Convergence, ct));
        refusal.Released.Should().BeFalse(
            "nothing released this address: a missing route here may be a live hub's stranded claim, and "
            + "its NACK must stay on the loud path");
    }

    /// <summary>
    /// Copilot thread 4473255558: two registrations of ONE address disposed concurrently. The
    /// successor marks its release and, before it removes its route, the predecessor is disposed too.
    /// With a single marker slot the predecessor's mark overwrote the successor's, and the
    /// predecessor's settle then cleared the address while the SUCCESSOR's release was still in flight.
    /// In production the predecessor's Detach can find the successor's live route and stamp no
    /// tombstone (#5136), so that cleared marker is the only cover, and the gap reopens.
    ///
    /// <para>Deterministic: the routing service's <c>AfterReleaseMarked</c> seam runs the
    /// predecessor's disposal inside exactly that window, the release hold holds both Detach calls,
    /// and <c>AfterReleaseSettled</c> is the positive signal that the predecessor's settle ran.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APredecessorsSettle_NeverClearsASuccessorsReleaseInFlight()
    {
        var ct = TestContext.Current.CancellationToken;
        var routing = mesh.Hub(Owner).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        var address = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"overlap-{Guid.NewGuid():N}");

        var predecessor = routing.RegisterStream(address, (d, _) => Observable.Return(d.Processed()));
        await (routing.PodHubClaimSettled(address) ?? Observable.Return(Unit.Default))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var successor = routing.RegisterStream(address, (d, _) => Observable.Return(d.Processed()));
        await (routing.PodHubClaimSettled(address) ?? Observable.Return(Unit.Default))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // The predecessor is disposed INSIDE the successor's disposal, so its Detach is issued first:
        // the first queued switch is the predecessor's, the second the successor's.
        var hold = mesh.Silo(Owner).GetRequiredService<ReleaseHold>();
        using var predecessorRelease = hold.Hold(address.ToString());
        using var successorRelease = hold.Hold(address.ToString());
        var settled = new ReplaySubject<Address>();
        var interleaved = 0;
        routing.AfterReleaseMarked = marked =>
        {
            if (marked == address && Interlocked.Exchange(ref interleaved, 1) == 0)
                predecessor.Dispose();
        };
        routing.AfterReleaseSettled = settled.OnNext;
        try
        {
            successor.Dispose();
            interleaved.Should().Be(1, "PRECONDITION: the predecessor was disposed inside the successor's window");
            await predecessorRelease.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the predecessor's Detach must be held, or this case injected nothing", ct);
            await successorRelease.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the successor's Detach must be held, or its release could settle on its own", ct);

            predecessorRelease.Release();
            await settled.Where(a => a == address).FirstAsync().Should().Within(TestTimeouts.Convergence)
                .Emit("the predecessor's release settles once its Detach has returned", ct);

            routing.IsReleaseInFlight(address).Should().BeTrue(
                "the SUCCESSOR's release is still held: a predecessor's settle must take out only its own "
                + "token, never the successor's mark (Copilot thread 4473255558)");
        }
        finally
        {
            routing.AfterReleaseMarked = null;
            routing.AfterReleaseSettled = null;
            predecessorRelease.Release();
            successorRelease.Release();
        }

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => routing.IsReleaseInFlight(address))
            .Where(inFlight => !inFlight)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence)
            .Emit("with both releases settled the address is no longer in flight — the marker is bounded", ct);
    }

    private static IMessageDelivery Delivery(Address address, string payload) =>
        new MessageDelivery<string>(payload, new PostOptions(address).WithTarget(address),
            JsonSerializerOptions.Default);

    /// <summary>Two multi-silo silos with the release hold.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 2;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloReleaseHoldConfigurator);
    }
}

/// <summary>Every silo a MULTI-SILO host (no memory stream), plus the pod-hub release hold.</summary>
public class MultiSiloReleaseHoldConfigurator : FaultInjectionSiloConfigurator
{
    /// <inheritdoc />
    protected override Action<ISiloBuilder>? PubSubStore =>
        silo => silo.AddMemoryGrainStorage(StreamProviders.PubSubStore);

    /// <inheritdoc />
    public override void Configure(ISiloBuilder siloBuilder)
    {
        base.Configure(siloBuilder);
        siloBuilder.Services.AddSingleton<ReleaseHold>();
        siloBuilder.AddOutgoingGrainCallFilter<ReleaseHoldFilter>();
    }
}

/// <summary>
/// Holds pod-hub releases (<c>IPodHubGrain.Detach</c>) of ONE address leaving this silo. Each
/// <see cref="Hold"/> queues one switch, and each Detach call of the address takes the next queued
/// switch, in call order, so two releases of the same address are held and let go independently.
/// One instance per silo (a DI singleton), never static.
/// </summary>
public sealed class ReleaseHold
{
    private ImmutableQueue<FaultSwitch> _queued = ImmutableQueue<FaultSwitch>.Empty;
    private string? _addressPath;

    /// <summary>Holds the next release of <paramref name="addressPath"/> until the returned switch is released.</summary>
    public FaultSwitch Hold(string addressPath)
    {
        // A new address starts a new queue: a switch a failed earlier case left unconsumed must not
        // be handed to this address's Detach.
        if (Interlocked.Exchange(ref _addressPath, addressPath) != addressPath)
            Volatile.Write(ref _queued, ImmutableQueue<FaultSwitch>.Empty);
        var fault = new FaultSwitch($"hold a pod-hub release of {addressPath}");
        ImmutableInterlocked.Enqueue(ref _queued, fault);
        return fault;
    }

    /// <summary>The switch the next Detach of the held address takes, or null when none is queued.</summary>
    internal FaultSwitch? TakeNext()
        => ImmutableInterlocked.TryDequeue(ref _queued, out var next) ? next : null;

    internal string? AddressPath => Volatile.Read(ref _addressPath);
}

/// <summary>
/// Applies <see cref="ReleaseHold"/> to the outgoing <c>IPodHubGrain.Detach</c> of the held address.
/// OUTGOING, on purpose: the hold sits on the caller's side, so the grain's turn stays free and the
/// test's delivery reaches the activation while the release has not.
/// </summary>
internal sealed class ReleaseHoldFilter(ReleaseHold hold) : IOutgoingGrainCallFilter
{
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (hold.AddressPath is { } held
            && context.InterfaceName.Contains(nameof(IPodHubGrain), StringComparison.Ordinal)
            && context.MethodName == nameof(IPodHubGrain.Detach)
            && context.TargetId.Key.ToString() == held
            && hold.TakeNext() is { IsClosed: true } fault)
            await fault.Gate(Observable.Return(Unit.Default), $"release of {held}").Await();
        await context.Invoke();
    }
}
