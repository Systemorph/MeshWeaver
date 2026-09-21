using System.Text.Json;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 THE SEAM TWO LAYERS DEPEND ON, pinned where it is stamped. A delivery's payload identity must
/// be readable AFTER <c>MessageDelivery.Package</c> has erased the payload to
/// <see cref="RawJson"/> — because that is the only shape the layers that read it ever see.
///
/// <list type="bullet">
///   <item><c>MessageStormBreaker</c> keys its rate counters on it, and without it N legitimate
///     cross-hub writes to N DISTINCT things fold into one bucket and the fan-out is DROPPED
///     (issue #1200).</item>
///   <item><c>RoutingGrain</c> keys <c>OrderedRouteDispatcher</c>'s ordered CHANNEL on it, and
///     without it a whole process's data-sync traffic shares one FIFO lane behind one in-flight
///     grain call (issue #5009).</item>
/// </list>
///
/// <para><b>Why this test and not an argument.</b> Both readers degrade SILENTLY to their
/// pre-identity behaviour when the key is absent — one bucket, one channel — which is correct as a
/// fallback and indistinguishable from a working fix from the outside. A stamp that quietly stopped
/// being applied (a new packaging path, a re-wrap, a property dropped on a hop) would leave both
/// layers looking healthy while doing the old thing. This asserts the stamp, the read, and the
/// post-wire-hop shape of the value.</para>
/// </summary>
public class DeliveryIdentitySurvivesPackagingTest
{
    private static readonly Address Sender = new("client", "1");
    private static readonly Address Target = new("cache", "12xX8OXQIEmdwvf_ZZ8LjA");
    private static readonly JsonSerializerOptions JsonOptions = new();

    private const string StreamId = "sync/one-particular-stream";

    /// <summary>Stands in for every <c>StreamMessage</c>: its identity IS its stream id.</summary>
    private record FrameOfAStream(string Stream, int Version) : IDiagnosticKeyed
    {
        public string DiagnosticKey => Stream;
    }

    /// <summary>A payload with nothing to say about what it is about.</summary>
    private record AnonymousMessage(int Seq);

    private static IMessageDelivery Delivery(object message)
        => new MessageDelivery<object>(Sender, Target, message, JsonOptions);

    [Fact]
    public void TypedPayload_IsReadDirectly_NoEnvelopeLookup()
    {
        DeliveryIdentity.Read(Delivery(new FrameOfAStream(StreamId, 7)))
            .Should().Be(StreamId,
                "a typed payload answers for itself — one interface check, no property lookup");
    }

    [Fact]
    public void PackagedPayload_IsReadOffTheEnvelope_AfterTheTypeIsGone()
    {
        var packaged = Delivery(new FrameOfAStream(StreamId, 7)).Package(JsonOptions);

        packaged.Message.Should().BeOfType<RawJson>(
            "precondition: packaging is what erases the type — this is the ONLY shape the router and "
            + "the receiving hub's ingestion gate ever see");
        packaged.Properties.ContainsKey(IDiagnosticKeyed.DeliveryProperty).Should().BeTrue(
            "Package stamps the identity onto the ENVELOPE, next to a Serialize that is already "
            + "happening — that stamp is the whole mechanism");

        DeliveryIdentity.Read(packaged).Should().Be(StreamId,
            "the stamped identity is what lets the routing layer give this stream its OWN ordered "
            + "channel without parsing the payload on the grain turn (#5009), and the storm breaker "
            + "its own rate bucket (#1200)");
    }

    [Fact]
    public void ValueArrivingAsJsonElement_AfterAWireHop_ReadsTheSame()
    {
        // What a JSON transport hop does to the property value: the stamped string comes back as a
        // JsonElement. Both readers must see through it, or the identity is lost at exactly the hop
        // it exists to survive.
        var packaged = Delivery(new FrameOfAStream(StreamId, 7)).Package(JsonOptions);
        var element = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(StreamId, JsonOptions), JsonOptions);
        var afterHop = packaged.SetProperty(IDiagnosticKeyed.DeliveryProperty, element);

        DeliveryIdentity.Read(afterHop).Should().Be(StreamId,
            "a JsonElement's ToString() is the string content — the same read PatchDataResponse's "
            + "RequestId correlation uses");
    }

    [Fact]
    public void PayloadWithNoIdentity_ReadsNull_SoEveryReaderKeepsItsOldBehaviour()
    {
        DeliveryIdentity.Read(Delivery(new AnonymousMessage(3))).Should().BeNull();
        DeliveryIdentity.Read(Delivery(new AnonymousMessage(3)).Package(JsonOptions)).Should().BeNull(
            "Package stamps nothing when the payload opts out, and null must degrade to the OLD, "
            + "stricter behaviour — one rate bucket, one destination-wide channel — never to a guess");
    }
}
