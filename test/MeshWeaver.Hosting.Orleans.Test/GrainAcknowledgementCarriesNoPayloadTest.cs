#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 AN ACKNOWLEDGEMENT IS NOT AN ECHO — issue #3045.
///
/// <para><b>The incident.</b> On 2026-09-02 <c>OrleansRoutingService</c> logged
/// <c>Failed to deliver to AppleMusic/_Issue/1059</c> with an <c>OutOfMemoryException</c> whose stack
/// is entirely on the RETURN leg: <c>InsideRuntimeClient.SafeSendResponse</c> →
/// <c>PooledResponseCopier.DeepCopy</c> → <c>JsonCodec.DeepCopy</c>. The callee could not send its
/// own answer. Nothing in the request path failed; the requesting hub simply never got its reply.</para>
///
/// <para><b>Why no existing guard covered it.</b> Five producer-side bounds had been placed by then —
/// the memory-stream publish and both forward grain legs inside <c>RoutingGrain</c> (#1890, #2897),
/// and <c>OrleansRoutingService</c>'s own <c>IRoutingGrain.RouteMessage</c> call (#2885) — and every
/// one of them measures a delivery on the way OUT. Not one of them had asked what the way BACK was
/// carrying, and the answer was: the same payload, again. All three of the mesh's Orleans delivery
/// legs are declared <c>Task&lt;IMessageDelivery&gt;</c> and return the delivery they were handed,
/// body included. Orleans copies a call's result with the same <c>JsonCodec</c> it copies the
/// arguments with, so an <i>n</i>-byte body cost <i>n</i> bytes out and <i>n</i> bytes back on every
/// hop.</para>
///
/// <para><b>And nobody read it.</b> <c>BuildPodHubRoute</c> discards the result outright
/// (<c>.Select(_ =&gt; Unit.Default)</c>); <c>BuildGrainRoute</c> and
/// <c>OrleansRoutingService.DispatchObservable</c> read <c>State</c>, <c>SenderWasNacked</c> and
/// <c>GetFailureMessage()</c> — the state and the properties, never <c>Message</c>. So the return
/// trip bought nothing at any size, which is why the strip is unconditional rather than measured
/// against a bound: a bound-conditional strip would keep paying this cost for every payload just
/// under it, and just under the bound is exactly where the production incident sat.</para>
///
/// <para><b>Driven through the real cluster, not around it.</b> These call the real grains through a
/// real two-silo <see cref="TwoSiloCacheUpdateFixture"/>, so the value asserted has been through
/// Orleans' actual copy/serialise path — which is the only place the defect existed. A pure test of
/// the helper would assert the fix and not the leg.</para>
/// </summary>
public class GrainAcknowledgementCarriesNoPayloadTest(TwoSiloCacheUpdateFixture fixture)
    : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The token that must not come back. Long enough that a truncated echo could not contain it by
    /// accident, and unique per run so a cached anything cannot supply it.
    /// </summary>
    private static readonly string PayloadMarker = $"payload-marker-{Guid.NewGuid():N}";

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    private static IMessageHub SiloMeshHub(TestCluster cluster, int index)
        => SiloServices(cluster, index).GetRequiredService<IMessageHub>();

    // A SILO's grain factory, never cluster.GrainFactory: this fixture deploys withClient:false, so
    // cluster.Client is null. Same reason as PodHubTransportTest.
    private static IGrainFactory Grains(TestCluster cluster, int index)
        => SiloServices(cluster, index).GetRequiredService<IGrainFactory>();

    /// <summary>
    /// A packaged delivery carrying <see cref="PayloadMarker"/> inside a body large enough that
    /// echoing it is a measurable cost — the shape the router actually sees, since
    /// <c>MeshBuilder</c> packages every delivery to <see cref="RawJson"/>.
    /// </summary>
    private static IMessageDelivery MarkedDelivery(Address sender, Address target)
    {
        var json = $"{{\"$type\":\"StaticRepoImportPayload\",\"marker\":\"{PayloadMarker}\","
            + $"\"nodes\":\"{new string('x', 200_000)}\"}}";
        return new MessageDelivery<RawJson>(
            sender, target, new RawJson(json), System.Text.Json.JsonSerializerOptions.Default);
    }

    /// <summary>
    /// What came back, as text — whatever shape the message arrives in after crossing the grain
    /// boundary (a <see cref="RawJson"/>, an untyped <c>JsonElement</c>, a typed message). Asserting
    /// on the TEXT rather than on a CLR type is deliberate: the claim is "the body did not travel",
    /// and that must hold however the envelope re-materialises on this side.
    /// </summary>
    private static string EchoedText(IMessageDelivery delivery) =>
        delivery.Message switch
        {
            null => string.Empty,
            RawJson raw => raw.Content ?? string.Empty,
            var m => m.ToString() ?? string.Empty,
        };

    /// <summary>
    /// 🚨 THE ROUTER LEG. <c>IRoutingGrain.RouteMessage</c> is the hop
    /// <c>OrleansRoutingService.DispatchObservable</c> makes for every delivery that is not served by
    /// a local route, and its acknowledgement is the value whose deep copy failed in production.
    ///
    /// <para>Against <c>origin/main</c> the marker comes straight back and this fails.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RouteMessage_acknowledges_without_echoing_the_body()
    {
        var cluster = fixture.Cluster;
        var sender = SiloMeshHub(cluster, 0).Address;
        var ghost = new Address("client", $"ghost-{Guid.NewGuid():N}");

        var ack = await Grains(cluster, 0).GetGrain<IRoutingGrain>("default")
            .RouteMessage(MarkedDelivery(sender, ghost))
            .WaitAsync(Budget, TestContext.Current.CancellationToken);

        ack.State.Should().Be(MessageDeliveryState.Forwarded,
            "the VERDICT is the whole point of the return value and must be unaffected — this is "
            + "the control that the strip removed the body and not the answer");
        EchoedText(ack).Should().NotContain(PayloadMarker,
            "the router's acknowledgement must not carry the body back. Orleans deep-copies a "
            + "grain call's RESULT with the same JsonCodec as its arguments, so echoing it made "
            + "every payload cross the boundary TWICE — and on 2026-09-02 the return half is the "
            + "half that ran out of memory, inside InsideRuntimeClient.SafeSendResponse, so the "
            + "callee could not even answer (#3045). No caller reads this body at any size");
    }

    /// <summary>
    /// 🚨 THE POD-HUB LEG, and the starkest of the three: <c>BuildPodHubRoute</c> discards the
    /// returned delivery entirely, so the body's whole return trip was spent on a value that is
    /// projected away one line later.
    ///
    /// <para>Asked from the silo that does NOT host the address, so the call is genuinely
    /// cross-silo and the response really is copied and framed. <c>mesh/{id}</c> is claimed for its
    /// process by <c>RootMeshHubReplyStreamService</c> at host start — the same address and the same
    /// claim <c>OrleansCrossSiloReplyTest</c> pins — so a <c>PodHubNotHereException</c> here would be
    /// that claim failing rather than anything this test is about.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task PodHubDeliver_acknowledges_without_echoing_the_body()
    {
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2,
            "asking the OWNING silo would be answered in-process, and an in-process answer is never "
            + "copied — the defect only exists once the response has to cross a silo boundary");

        var rootA = SiloMeshHub(cluster, 0).Address;

        var ack = await Grains(cluster, 1).GetGrain<IPodHubGrain>(rootA.ToString())
            .Deliver(MarkedDelivery(rootA, rootA))
            .WaitAsync(Budget, TestContext.Current.CancellationToken);

        ack.State.Should().Be(MessageDeliveryState.Forwarded,
            "the hand-off outcome — the one thing a stream publish could never confirm — must "
            + "survive the strip");
        EchoedText(ack).Should().NotContain(PayloadMarker,
            "BuildPodHubRoute projects this result away with .Select(_ => Unit.Default), so the "
            + "body's return trip was pure cost: an Orleans JsonCodec deep copy of the whole "
            + "payload plus a frame back across the wire, for a value nobody looks at (#3045)");
    }

    /// <summary>
    /// The delivery still ARRIVES. The strip is on the way back only — if it reached the forward
    /// argument, every message in the mesh would be delivered empty, which is a far worse defect
    /// than the one being fixed. This is the fact that could falsify that.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_forward_body_is_untouched_by_the_acknowledgement_strip()
    {
        var cluster = fixture.Cluster;
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("client", $"ackprobe-{Guid.NewGuid():N}");

        // The producer→test signal, per AGENTS.md: an AsyncSubject the producer completes, awaited
        // through the sanctioned bridge with the timeout at the call site. Never a
        // TaskCompletionSource (a hand-woven gate) and never .ToTask() (resumes inline on the
        // signalling thread — here, the pod-hub grain's own turn).
        var received = new AsyncSubject<IMessageDelivery>();

        var routing = (OrleansRoutingService)SiloServices(cluster, 0)
            .GetRequiredService<IRoutingService>();
        using var registration = routing.RegisterStream(address, (d, _) =>
        {
            received.OnNext(d);
            received.OnCompleted();
            return Observable.Return(d);
        });

        var ack = await Grains(cluster, 0).GetGrain<IPodHubGrain>(address.ToString())
            .Deliver(MarkedDelivery(address, address))
            .WaitAsync(Budget, ct);

        ack.State.Should().Be(MessageDeliveryState.Forwarded);

        var delivered = await received.Timeout(Budget).Await(ct);
        EchoedText(delivered).Should().Contain(PayloadMarker,
            "the RECEIVING hub must still be handed the whole body — the acknowledgement is what "
            + "loses the payload, never the delivery. If the strip ever reached the forward "
            + "argument, every message in the mesh would arrive empty, which is a far worse defect "
            + "than the one being fixed; this is the fact that could show it");
    }
}
