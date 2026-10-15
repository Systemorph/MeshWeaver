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
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>#4802 slice 4 — on a host with no memory stream, a fresh hub's first answer must not overtake
/// its pod-hub claim.</b>
///
/// <para>There the claim is the hub's ONLY inbound cross-process transport, and it lands
/// asynchronously (after Active, then a grain call). An answer from another silo that arrives first
/// meets an <c>IPodHubGrain</c> activation with no local route, is refused <c>PodHubNotHere</c>, and is
/// lost — the requester waits out its budget. With a memory stream the outbound dispatch was gated on
/// the stream attach (#1081); without one it is gated on the claim
/// (<c>OrleansRoutingService.podHubOutboundReady</c>).</para>
///
/// <para><b>The fault, deterministically.</b> Two multi-silo silos (no memory stream). A responder hub
/// on silo 1 echoes every message back to its sender. A fresh sender hub on silo 0 registers while its
/// claim's <c>Attach</c> call is HELD (the hold's arrival proves the claim is pending), and pings the
/// responder. Then the hold lets go.</para>
/// <list type="bullet">
///   <item><b>Fixed:</b> the ping leaves through the sender's own router, which holds it until the
///     claim lands — so the echo finds the claim and the sender hears it.</item>
///   <item><b>Negative control:</b> the identical sequence, but the ping is handed straight to the
///     routing grain — the ungated shape. The echo overtakes the held claim, is refused
///     <c>PodHubNotHere</c> on silo 1, and the sender never hears it, even after the claim lands. This
///     proves the race is real and that the fixed arm's arrival is the gate's doing.</item>
/// </list>
/// </summary>
public abstract class AFreshHubsAnswerAndItsClaim(FaultInjectionCluster mesh)
{
    private const int Owner = 0;
    private const int Responder = 1;

    /// <summary>Whether the ping leaves through the sender's gated router (fixed) or straight to the routing grain.</summary>
    protected abstract bool SentThroughTheGatedRouter { get; }

    [Fact(Timeout = 120_000)]
    public async Task TheFirstAnswer_ReachesAFreshHub_WhoseClaimWasPending()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerRouting = mesh.Hub(Owner).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        var responderRouting = mesh.Hub(Responder).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        ownerRouting.HasMemoryStream.Should().BeFalse(
            "with a memory stream the outbound gate is the stream attach and this case would prove nothing");

        // ── The responder on silo 1: echoes every message back to its sender ────────────────────────
        var responder = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"echo-{Guid.NewGuid():N}");
        using var responderRegistration = responderRouting.RegisterStream(responder, (d, _) =>
        {
            // Only the ping is answered: the transient NACK for a refused echo also arrives here, and
            // echoing THAT would re-send the answer until the claim lands — a retry the real mesh
            // does not perform, which would carry the negative control.
            if (d.Message is PingRequest && d.Sender is { } back)
                responderRouting.DeliverMessage(new MessageDelivery<PingRequest>(
                        new PingRequest(), new PostOptions(responder).WithTarget(back),
                        System.Text.Json.JsonSerializerOptions.Default))
                    .Subscribe(_ => { }, _ => { });
            return Observable.Return(d.Processed());
        });
        await (responderRouting.PodHubClaimSettled(responder) ?? Observable.Return(Unit.Default))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // ── The fresh sender on silo 0, its claim HELD ──────────────────────────────────────────────
        var sender = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"fresh-{Guid.NewGuid():N}");
        var hold = mesh.Silo(Owner).GetRequiredService<ClaimHold>();
        using var claim = hold.Hold(sender.ToString());
        var heard = new ReplaySubject<IMessageDelivery>();
        using var registration = ownerRouting.RegisterStream(sender, (d, _) =>
        {
            heard.OnNext(d);
            return Observable.Return(d.Processed());
        });
        await claim.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the sender's claim must be in flight and held — otherwise this case injected nothing", ct);

        var ping = new MessageDelivery<PingRequest>(
            new PingRequest(), new PostOptions(sender).WithTarget(responder),
            System.Text.Json.JsonSerializerOptions.Default);
        if (SentThroughTheGatedRouter)
            ownerRouting.DeliverMessage(ping).Subscribe(_ => { }, _ => { });
        else
            await mesh.Silo(Owner).GetRequiredService<IGrainFactory>().GetGrain<IRoutingGrain>("default")
                .RouteMessage(ping).WaitAsync(TestTimeouts.Convergence, ct);

        var echo = heard.Where(d => d.Sender == responder);
        // Both arms hold the claim long enough for an ungated ping's echo to reach silo 1 and be
        // refused there — releasing earlier would let the echo find the claim by luck, not by the gate.
        await echo.Should().NotEmit(TimeSpan.FromSeconds(3),
            SentThroughTheGatedRouter
                ? "the ping is held at the sender's router until its claim lands"
                : "the echo overtakes the held claim and is refused on silo 1", ct);

        claim.Release();

        if (SentThroughTheGatedRouter)
            await echo.Should().Within(TestTimeouts.Convergence)
                .Emit("the ping was held until the sender's claim landed, so the echo found it (#4802)", ct);
        else
            await echo.Should().NotEmit(TimeSpan.FromSeconds(5),
                "nothing re-sends an answer refused PodHubNotHere — if this arm ever hears it, something "
                + "other than the gate carries the fixed arm and it proves nothing", ct);
    }
}

/// <summary>The fix: the fresh hub's ping is held until its claim lands.</summary>
public class AFreshHubsFirstAnswerWaitsForItsClaimTest(AFreshHubsFirstAnswerWaitsForItsClaimTest.Cluster mesh)
    : AFreshHubsAnswerAndItsClaim(mesh), IClassFixture<AFreshHubsFirstAnswerWaitsForItsClaimTest.Cluster>
{
    protected override bool SentThroughTheGatedRouter => true;

    /// <summary>Two multi-silo silos with the claim hold.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 2;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloClaimHoldConfigurator);
    }
}

/// <summary>Negative control: an answer that overtakes the claim is lost.</summary>
public class AnAnswerThatOvertakesTheClaimIsLostTest(AnAnswerThatOvertakesTheClaimIsLostTest.Cluster mesh)
    : AFreshHubsAnswerAndItsClaim(mesh), IClassFixture<AnAnswerThatOvertakesTheClaimIsLostTest.Cluster>
{
    protected override bool SentThroughTheGatedRouter => false;

    /// <summary>Two multi-silo silos with the claim hold.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 2;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloClaimHoldConfigurator);
    }
}

/// <summary>Every silo a MULTI-SILO host (no memory stream), plus the pod-hub claim hold.</summary>
public class MultiSiloClaimHoldConfigurator : FaultInjectionSiloConfigurator
{
    /// <inheritdoc />
    protected override Action<ISiloBuilder>? PubSubStore =>
        silo => silo.AddMemoryGrainStorage(StreamProviders.PubSubStore);

    /// <inheritdoc />
    public override void Configure(ISiloBuilder siloBuilder)
    {
        base.Configure(siloBuilder);
        siloBuilder.Services.AddSingleton<ClaimHold>();
        siloBuilder.AddOutgoingGrainCallFilter<ClaimHoldFilter>();
    }
}

/// <summary>
/// Holds the pod-hub claim (<c>IPodHubGrain.Attach</c>) of ONE address leaving this silo until the
/// returned switch is released. One instance per silo (a DI singleton), never static.
/// </summary>
public sealed class ClaimHold
{
    private FaultSwitch? _hold;
    private string? _addressPath;

    /// <summary>Holds the claim of <paramref name="addressPath"/> until the returned switch is released.</summary>
    public FaultSwitch Hold(string addressPath)
    {
        Volatile.Write(ref _addressPath, addressPath);
        var fault = new FaultSwitch($"hold the pod-hub claim of {addressPath}");
        Volatile.Write(ref _hold, fault);
        return fault;
    }

    internal FaultSwitch? Current => Volatile.Read(ref _hold);

    internal string? AddressPath => Volatile.Read(ref _addressPath);
}

/// <summary>Applies <see cref="ClaimHold"/> to the outgoing <c>IPodHubGrain.Attach</c> of the held address.</summary>
internal sealed class ClaimHoldFilter(ClaimHold hold) : IOutgoingGrainCallFilter
{
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (hold.Current is { IsClosed: true } fault
            && hold.AddressPath is { } held
            && context.InterfaceName.Contains(nameof(IPodHubGrain), StringComparison.Ordinal)
            && context.MethodName == nameof(IPodHubGrain.Attach)
            && context.TargetId.Key.ToString() == held)
            await fault.Gate(Observable.Return(Unit.Default), $"claim of {held}").Await();
        await context.Invoke();
    }
}
