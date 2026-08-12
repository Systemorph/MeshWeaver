using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic unit tests for the universal <see cref="MessageStormBreaker"/> — the
/// hub-ingestion circuit-breaker that detects an unbounded retry/resubscribe/repost loop
/// (the SAME <c>(sender, target, type)</c> tuple at a rate no legitimate single-key
/// traffic can reach) and drops it before the single-threaded turn loop saturates.
///
/// <para>Time is injected as a logical clock so the rate window, trip, cooldown and
/// self-heal transitions are driven by advancing a counter — never by wall-clock sleeps,
/// so there is no CI-load flakiness.</para>
/// </summary>
public class MessageStormBreakerTest
{
    // 1 tick == 1 millisecond in these tests (ticksPerSecond = 1000). The breaker is
    // configured with a 1s window / threshold 5 / 1s cooldown so the assertions are tiny.
    private const long TicksPerSecond = 1000;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1);
    private const int Threshold = 5;

    private static readonly Address Sender = new("client", "1");
    private static readonly Address Target = new("host", "1");
    private static readonly JsonSerializerOptions JsonOptions = new();

    private long _now;

    private MessageStormBreaker CreateBreaker()
        => new(NullLogger.Instance, new Address("host", "1"),
            Threshold, Window, Cooldown, () => _now, TicksPerSecond);

    private static IMessageDelivery Delivery(object message, Address? sender = null, Address? target = null)
        => new MessageDelivery<object>(sender ?? Sender, target ?? Target, message, JsonOptions);

    // A plain message type with no [CanBeIgnored] / lifecycle exemption — the storm-prone
    // path (stands in for SubscribeRequest / RawJson and friends). It exposes NO payload
    // identity, so it exercises the fail-closed fallback: keyed on the routing tuple alone.
    private record StormableMessage(int Seq = 0);

    // [CanBeIgnored] fire-and-forget control traffic — must be exempt from the breaker.
    [CanBeIgnored]
    private record IgnorableControlMessage(int Seq = 0);

    // Stands in for the real wide-traffic shapes — DataChangedEvent (StreamId),
    // CreateOrUpdateNodeRequest (node path): ONE sender, ONE target, ONE message type, but each
    // message is ABOUT a different thing.
    private record KeyedMessage(string About) : IDiagnosticKeyed
    {
        public string DiagnosticKey => About;
    }

    [Fact]
    public void Trips_AndDrops_WhenOneKeyExceedsThresholdInWindow()
    {
        var breaker = CreateBreaker();
        var trips = new List<MessageStormBreaker.StormTrip>();
        using var _ = breaker.Trips.Subscribe(trips.Add);

        // The first `Threshold` messages pass; the (Threshold+1)-th crosses the bar and trips.
        for (var i = 0; i < Threshold; i++)
            breaker.ShouldDrop(Delivery(new StormableMessage(i))).Should().BeFalse(
                "messages under the threshold must pass untouched");

        breaker.ShouldDrop(Delivery(new StormableMessage(Threshold))).Should().BeTrue(
            "the message that crosses the threshold is dropped");

        // Every subsequent identical-key message in the storm is dropped.
        for (var i = 0; i < 50; i++)
            breaker.ShouldDrop(Delivery(new StormableMessage(100 + i))).Should().BeTrue(
                "all further messages of the storming key are dropped");

        // Exactly ONE trip transition (and therefore one Error log) — not one per drop.
        breaker.TripCount.Should().Be(1, "the breaker trips and logs once per storm, not per dropped message");
        trips.Should().ContainSingle();
        trips[0].Sender.Should().Be(Sender);
        trips[0].Target.Should().Be(Target);
        trips[0].TypeName.Should().Be(nameof(StormableMessage));
    }

    [Fact]
    public void DiverseTraffic_HighTotalVolume_NeverTrips()
    {
        var breaker = CreateBreaker();
        var tripped = false;
        using var _ = breaker.Trips.Subscribe(_ => tripped = true);

        // 10x the threshold in TOTAL volume, but spread across distinct senders — i.e.
        // DIVERSE keys, each well under the per-key threshold. This is the shape of real
        // load (many callers, modest per-key rate). None of it may trip.
        const int senders = 200;
        const int perSender = Threshold; // exactly at-but-not-over the per-key bar
        for (var s = 0; s < senders; s++)
        {
            var sender = new Address("client", s.ToString());
            for (var i = 0; i < perSender; i++)
                breaker.ShouldDrop(Delivery(new StormableMessage(i), sender: sender))
                    .Should().BeFalse("diverse keys under the per-key rate must never be dropped");
        }

        tripped.Should().BeFalse("high diverse volume must not be mistaken for a storm");
        breaker.TripCount.Should().Be(0);
    }

    [Fact]
    public void SelfHeals_AfterStormStops_KeyFlowsAgain()
    {
        var breaker = CreateBreaker();

        // Storm the key in window 0 → trip.
        for (var i = 0; i <= Threshold; i++)
            breaker.ShouldDrop(Delivery(new StormableMessage(i)));
        breaker.TripCount.Should().Be(1);
        breaker.ShouldDrop(Delivery(new StormableMessage())).Should().BeTrue("still storming → still dropped");

        // The loop stops. Advance past the window AND the cooldown with NO traffic, then
        // roll the window once (a single message) so the breaker observes the quiet window.
        _now += (long)(Window.TotalSeconds * TicksPerSecond) + (long)(Cooldown.TotalSeconds * TicksPerSecond) + 1;

        // First message in the fresh, quiet window self-heals the key — it must flow.
        breaker.ShouldDrop(Delivery(new StormableMessage())).Should().BeFalse(
            "once the per-key rate falls back under threshold the key self-heals and flows again");

        // And normal-cadence traffic keeps flowing.
        for (var i = 0; i < Threshold - 1; i++)
            breaker.ShouldDrop(Delivery(new StormableMessage(i))).Should().BeFalse();

        breaker.TripCount.Should().Be(1, "self-heal must not log a second trip");
    }

    /// <summary>
    /// 🚨 The #1200 defect. A bulk import drives ONE sender → ONE target with ONE message type
    /// over thousands of DISTINCT things (every mesh-node path it writes, every sync stream those
    /// writes open). Keyed on the routing tuple alone that whole legitimate fan-out is one bucket,
    /// crosses the per-key bar, and the breaker DISCARDS real writes at ingestion — imported
    /// content silently missing, which is data loss, not mitigation.
    ///
    /// <para>RED before the payload-identity component (every message folds into one key and the
    /// breaker trips); GREEN after (each thing is counted on its own key, all far under the bar).</para>
    /// </summary>
    [Fact]
    public void WideFanOut_OneTuple_ManyDistinctPayloadKeys_NeverTrips()
    {
        var breaker = CreateBreaker();
        var trips = new List<MessageStormBreaker.StormTrip>();
        using var _ = breaker.Trips.Subscribe(trips.Add);

        // 40x the per-key threshold in TOTAL volume through a SINGLE (sender, target, type)
        // tuple — the cross-hub dispatcher shape — but every message is about a different path.
        const int distinctPaths = Threshold * 40;
        for (var i = 0; i < distinctPaths; i++)
            breaker.ShouldDrop(Delivery(new KeyedMessage($"UWDeepfield/Content/node-{i}")))
                .Should().BeFalse(
                    "a write to a DISTINCT path is not a loop, however many of them one importer issues");

        trips.Should().BeEmpty("a wide fan-out over one tuple must never be mistaken for a storm");
        breaker.TripCount.Should().Be(0);
    }

    /// <summary>
    /// The guard itself is unchanged: concentrate the SAME volume on ONE thing and it still trips —
    /// and the trip now names WHICH thing, which is what turns "something between these two hubs
    /// is looping" into a pointer at the offending stream/path.
    /// </summary>
    [Fact]
    public void RepeatedSamePayloadKey_StillTrips_AndNamesTheThing()
    {
        var breaker = CreateBreaker();
        var trips = new List<MessageStormBreaker.StormTrip>();
        using var _ = breaker.Trips.Subscribe(trips.Add);

        const string looping = "UWDeepfield/_Activity/import-f2bb979af363573d";
        for (var i = 0; i < Threshold; i++)
            breaker.ShouldDrop(Delivery(new KeyedMessage(looping))).Should().BeFalse();

        breaker.ShouldDrop(Delivery(new KeyedMessage(looping))).Should().BeTrue(
            "one thing repeated past the per-key bar IS the loop the breaker exists to stop");

        trips.Should().ContainSingle();
        trips[0].PayloadKey.Should().Be(looping, "the trip must name what the storm was about");
        trips[0].TypeName.Should().Be(nameof(KeyedMessage));

        // And the fan-out running ALONGSIDE the loop keeps flowing — the drop is scoped to the
        // one storming thing, not to everything that shares its sender/target/type.
        for (var i = 0; i < Threshold; i++)
            breaker.ShouldDrop(Delivery(new KeyedMessage($"UWDeepfield/Content/other-{i}")))
                .Should().BeFalse("healthy traffic on the same tuple is untouched by another key's trip");
    }

    /// <summary>
    /// A cross-hub delivery reaches the receiving hub's ingestion gate UNDESERIALIZED — the
    /// breaker sees <c>type=RawJson</c> and must not parse the payload on the hottest path in the
    /// hub. <c>Package</c> stamps the identity onto the ENVELOPE right before it erases the type,
    /// so discrimination survives the hop: the same fan-out that would fold into one key after
    /// packaging still counts per thing.
    /// </summary>
    [Fact]
    public void PackagedDelivery_KeepsPayloadIdentity_AcrossTheTypeErasure()
    {
        var breaker = CreateBreaker();
        var trips = new List<MessageStormBreaker.StormTrip>();
        using var _ = breaker.Trips.Subscribe(trips.Add);

        // Post-Package the message really is RawJson — the state the cache hub saw in #1200.
        var packagedSample = Delivery(new KeyedMessage("about/one")).Package();
        packagedSample.Message.Should().BeOfType<RawJson>("Package erases the payload type");

        const int distinctPaths = Threshold * 40;
        for (var i = 0; i < distinctPaths; i++)
            breaker.ShouldDrop(Delivery(new KeyedMessage($"UWDeepfield/Content/node-{i}")).Package())
                .Should().BeFalse("the envelope carries the identity the erased payload no longer shows");

        trips.Should().BeEmpty();

        // ...while a packaged LOOP on one thing still trips, still named.
        for (var i = 0; i <= Threshold; i++)
            breaker.ShouldDrop(Delivery(new KeyedMessage("looping/stream")).Package());

        trips.Should().ContainSingle();
        trips[0].TypeName.Should().Be(nameof(RawJson), "after the hop the type really is RawJson");
        trips[0].PayloadKey.Should().Be("looping/stream");
    }

    /// <summary>
    /// The fallback is FAIL-CLOSED, never "allow". A message exposing no identity (and a packaged
    /// delivery whose sender stamped none) keys on exactly the pre-#1200
    /// <c>(sender, target, type)</c> tuple and trips exactly as it always did.
    /// </summary>
    [Fact]
    public void NoPayloadIdentity_FallsBackToTheRoutingTuple_AndStillTrips()
    {
        var breaker = CreateBreaker();
        var trips = new List<MessageStormBreaker.StormTrip>();
        using var _ = breaker.Trips.Subscribe(trips.Add);

        // Distinct CONTENT (Seq differs) but no IDiagnosticKeyed → one key, exactly as before.
        for (var i = 0; i < Threshold; i++)
            breaker.ShouldDrop(Delivery(new StormableMessage(i))).Should().BeFalse();
        breaker.ShouldDrop(Delivery(new StormableMessage(Threshold))).Should().BeTrue(
            "an unidentifiable payload must keep the old, stricter behaviour — never be waved through");

        trips.Should().ContainSingle();
        trips[0].PayloadKey.Should().BeNull("nothing identified this payload; the key degrades to the tuple");

        // Same for a packaged one: Package stamps nothing when the message opts out.
        var breaker2 = CreateBreaker();
        for (var i = 0; i < Threshold; i++)
            breaker2.ShouldDrop(Delivery(new StormableMessage(i)).Package()).Should().BeFalse();
        breaker2.ShouldDrop(Delivery(new StormableMessage(Threshold)).Package()).Should().BeTrue();
    }

    /// <summary>
    /// The payload component makes the key space unbounded in principle (one counter per thing the
    /// hub ever saw), so the live set must track RECENT traffic, not all-time traffic. Crossing the
    /// soft cap arms one inline sweep per window that drops counters idle beyond window+cooldown —
    /// no timer, no background thread, no static state, and no effect on detection (an idle key is
    /// by definition not storming).
    /// </summary>
    [Fact]
    public void TrackedKeys_AreBounded_ByRecentTrafficNotAllTimeTraffic()
    {
        const int cap = 10;
        var breaker = new MessageStormBreaker(NullLogger.Instance, new Address("host", "1"),
            Threshold, Window, Cooldown, () => _now, TicksPerSecond, maxTrackedKeys: cap);

        // A burst of one-shot keys — the shape that used to accumulate forever.
        const int oneShotKeys = 500;
        for (var i = 0; i < oneShotKeys; i++)
            breaker.ShouldDrop(Delivery(new KeyedMessage($"burst/node-{i}"))).Should().BeFalse();

        breaker.TrackedKeyCount.Should().Be(oneShotKeys, "each distinct thing is counted on its own key");

        // They go quiet. Past window+cooldown the next message arms the sweep and they are gone.
        _now += (long)((Window.TotalSeconds + Cooldown.TotalSeconds) * TicksPerSecond) + 1;
        breaker.ShouldDrop(Delivery(new KeyedMessage("later/node"))).Should().BeFalse();

        breaker.TrackedKeyCount.Should().Be(1,
            "counters idle past window+cooldown are swept, so the live set follows recent traffic");
        breaker.TripCount.Should().Be(0, "sweeping idle keys must not look like — or mask — a storm");
    }

    [Fact]
    public void LifecycleAndControlMessages_AreNeverDropped()
    {
        var breaker = CreateBreaker();

        var inner = Delivery(new StormableMessage());

        // Lifecycle / control traffic must pass even when hammered far past the threshold —
        // dropping it could deadlock teardown or init. (ShutdownRequest is internal to the
        // hub assembly, so it isn't exercised here directly; the breaker exempts it by type
        // alongside these — see MessageStormBreaker.ShouldDrop.)
        for (var i = 0; i < Threshold * 4; i++)
        {
            breaker.ShouldDrop(Delivery(new DisposeRequest())).Should().BeFalse();
            breaker.ShouldDrop(Delivery(new HeartBeatEvent())).Should().BeFalse();
            breaker.ShouldDrop(Delivery(new InitializeHubRequest())).Should().BeFalse();
            breaker.ShouldDrop(Delivery(new DeliveryFailure(inner, "boom"))).Should().BeFalse();
            // Attribute-based exemption: any [CanBeIgnored] type, even one storming.
            breaker.ShouldDrop(Delivery(new IgnorableControlMessage(i))).Should().BeFalse();
        }

        breaker.TripCount.Should().Be(0,
            "lifecycle/control traffic is exempt and must never trip the breaker");
    }

    /// <summary>
    /// Invariant 3 boundary (Doc/Architecture/ActionBlockWedgePrevention.md): the per-hub
    /// aggregate watermark sheds ONLY sheddable ([CanBeIgnored], non-lifecycle) traffic, and
    /// only once the inbound depth has crossed the line. User-facing application messages and
    /// lifecycle/control are NEVER shed, however deep the overload — dropping those would
    /// strand a requester or deadlock teardown/init.
    /// </summary>
    [Fact]
    public void Aggregate_ShedsOnlySheddable_AboveWatermark()
    {
        const int watermark = 10;
        var breaker = new MessageStormBreaker(NullLogger.Instance, new Address("host", "1"),
            Threshold, Window, Cooldown, () => _now, TicksPerSecond, aggregateWatermark: watermark);

        var inner = Delivery(new StormableMessage());

        // Below the watermark nothing is shed, even sheddable traffic.
        breaker.ShouldShedAggregate(Delivery(new IgnorableControlMessage()), inboundDepth: watermark - 1)
            .Should().BeFalse("under the watermark the block is draining fine — shed nothing");

        // At/above the watermark, sheddable [CanBeIgnored] traffic IS shed.
        breaker.ShouldShedAggregate(Delivery(new IgnorableControlMessage()), inboundDepth: watermark)
            .Should().BeTrue("at the watermark, sheddable fire-and-forget traffic is shed to keep draining");

        // User-facing (non-[CanBeIgnored]) is NEVER shed, however deep the overload.
        breaker.ShouldShedAggregate(Delivery(new StormableMessage()), inboundDepth: 10_000)
            .Should().BeFalse("user-facing application messages are never shed");

        // TRUE lifecycle / control is NEVER shed (dropping it deadlocks teardown/init).
        breaker.ShouldShedAggregate(Delivery(new DisposeRequest()), 10_000).Should().BeFalse();
        breaker.ShouldShedAggregate(Delivery(new InitializeHubRequest()), 10_000).Should().BeFalse();
        breaker.ShouldShedAggregate(Delivery(new DeliveryFailure(inner, "boom")), 10_000).Should().BeFalse();

        // HeartBeatEvent IS shed under overload — it is periodic grain keep-alive, NOT lifecycle. At the
        // watermark the grain is busy draining a deep queue (not idle), so a dropped keep-alive can't
        // idle-deactivate it; shedding stops heartbeats piling into an already-overloaded turn loop. This
        // is the fix for "heartbeats must not pile up; if they can't be delivered under load, trash them".
        breaker.ShouldShedAggregate(Delivery(new HeartBeatEvent()), 10_000)
            .Should().BeTrue("heartbeats must be sheddable under overload so they don't accumulate");

        breaker.AggregateShedCount.Should().Be(2, "the sheddable control message and the heartbeat were both shed");
    }
}
