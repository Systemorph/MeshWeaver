#pragma warning disable CS1591

using System;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 A same-silo grain call hands the delivery envelope across BY REFERENCE — issue #4824.
///
/// <para>Orleans deep-copies a local grain call's arguments and result with whatever copier the
/// serializer resolves for the runtime type. The mesh had given that job to <c>JsonCodec</c> for
/// every type, so each local <c>RouteMessage</c> / <c>DeliverMessage</c> / <c>Deliver</c> serialised
/// its delivery to JSON and parsed it back — the allocation the production out-of-memory stacks sit
/// in (<c>JsonCodec.DeepCopy</c>, <c>ObjectPolymorphicConverter.Write</c>). The envelope is immutable
/// and its crossing payload is <c>RawJson</c>, so the copy isolates nothing.</para>
///
/// <para>Asserted through the SILO's own <see cref="DeepCopier"/> — the serializer configuration
/// the running cluster actually resolved — never a hand-built one, so a registration that did not
/// reach the silo fails here.</para>
/// </summary>
public class LocalGrainCallSharesTheEnvelopeTest(TwoSiloCacheUpdateFixture fixture)
    : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private static DeepCopier SiloCopier(TestCluster cluster)
        => ((InProcessSiloHandle)cluster.Silos[0]).SiloHost.Services.GetRequiredService<DeepCopier>();

    /// <summary>A packaged delivery — the shape every delivery has when it reaches a grain call.</summary>
    private static IMessageDelivery PackagedDelivery()
        => new MessageDelivery<RawJson>(
            new Address("client", $"sender-{Guid.NewGuid():N}"),
            new Address("client", $"target-{Guid.NewGuid():N}"),
            new RawJson($"{{\"$type\":\"Probe\",\"body\":\"{new string('x', 100_000)}\"}}"),
            System.Text.Json.JsonSerializerOptions.Default);

    [Fact]
    public void A_delivery_is_not_copied_through_json_on_a_local_call()
    {
        var delivery = PackagedDelivery();

        var copied = SiloCopier(fixture.Cluster).Copy(delivery);

        ReferenceEquals(copied, delivery).Should().BeTrue(
            "the delivery envelope is immutable and its payload is RawJson, so a same-silo grain "
            + "call must hand the same instance across. A different instance means the JsonCodec "
            + "round trip still runs — serialise the whole payload, parse it back — on every local "
            + "hop, which is the allocation the out-of-memory stacks of #4824 and #3045 sit in");
    }

    /// <summary>
    /// 🚨 An UN-packaged delivery keeps the isolating copy: its typed payload may be mutable, and
    /// the immutability argument only covers the packaged <c>RawJson</c> shape.
    /// </summary>
    [Fact]
    public void An_unpackaged_delivery_is_still_copied()
    {
        // The payload type is what selects the copier, so any un-packaged message shows it.
        var delivery = new MessageDelivery<string>(
            new Address("client", $"sender-{Guid.NewGuid():N}"),
            new Address("client", $"target-{Guid.NewGuid():N}"),
            "an un-packaged message",
            System.Text.Json.JsonSerializerOptions.Default);

        var copied = SiloCopier(fixture.Cluster).Copy<IMessageDelivery>(delivery);

        ReferenceEquals(copied, delivery).Should().BeFalse(
            "a delivery that was not packaged may carry a mutable CLR payload, so it must not be "
            + "aliased across a grain call — only the packaged RawJson shape is immutable end to end");
    }

    /// <summary>
    /// 🚨 THE CONTROL: the silo's copier still COPIES what is not an envelope, so the assertion above
    /// is about the envelope and not about a copier that stopped copying everything.
    /// </summary>
    [Fact]
    public void Anything_else_is_still_copied()
    {
        var mutable = new System.Text.Json.Nodes.JsonObject { ["a"] = 1 };

        var copied = SiloCopier(fixture.Cluster).Copy(mutable);

        ReferenceEquals(copied, mutable).Should().BeFalse(
            "a mutable non-envelope value must still be isolated by a deep copy");
    }
}
