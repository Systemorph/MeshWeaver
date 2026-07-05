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
    // path (stands in for SubscribeRequest / RawJson and friends).
    private record StormableMessage(int Seq = 0);

    // [CanBeIgnored] fire-and-forget control traffic — must be exempt from the breaker.
    [CanBeIgnored]
    private record IgnorableControlMessage(int Seq = 0);

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
