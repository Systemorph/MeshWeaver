using System;
using System.Linq;
using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Systemorph/MeshWeaver#5532 — a WAVE of mirrors must not heartbeat in lock-step.
///
/// <para>Every mirror posts a <c>HeartBeatEvent</c> through the pod's mesh hub (one single-threaded
/// queue) and an owner that does not serve the named stream answers with a <c>StreamEndedEvent</c>
/// through the same queue. With one shared first tick and one shared period, N mirrors opened in one
/// second are N beats (and up to N answers) in ONE turn window, forever. memex-cloud 2026-11-21T09:29Z
/// crossed the 10000 aggregate watermark that way, 27 of 30 shed lines naming <c>HeartBeatEvent</c>.
/// These tests pin the SCHEDULE: how many beats of an N-stream wave can land in one 100 ms window of
/// the mesh hub's turn loop, and that the spread never makes a beat LATER than configured.</para>
/// </summary>
public class HeartbeatWaveSpreadTest
{
    private const int Wave = 3000;
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FirstHeartbeat = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

    // FIXED ids (a deterministic Guid per index), so the bound is a replayable pin, never a lottery.
    private static string[] StreamIds(int n) => Enumerable.Range(0, n)
        .Select(i => new Guid(i, 0x5532, 0x1a2b, 1, 2, 3, 4, 5, 6, 7, 8).ToString("N")).ToArray();

    /// <summary>The ONE call the heartbeat timer makes keeps the floor-lowering: a short cadence is
    /// never delayed to the first-beat floor, and a long one gets the early first tick.</summary>
    [Fact]
    public void For_KeepsTheFloorLoweringAndSpreadsBoth()
    {
        var (first, period) = HeartbeatSchedule.For("s1", TimeSpan.FromMilliseconds(200), FirstHeartbeat);
        Assert.True(first <= TimeSpan.FromMilliseconds(200) && period <= TimeSpan.FromMilliseconds(200));
        var (longFirst, longPeriod) = HeartbeatSchedule.For("s1", Interval, FirstHeartbeat);
        Assert.True(longFirst <= FirstHeartbeat && longPeriod <= Interval);
        Assert.Equal(HeartbeatSchedule.Spread("s1", FirstHeartbeat, HeartbeatSchedule.FirstTickSpread), longFirst);
        Assert.Equal(HeartbeatSchedule.Spread("s1", Interval, HeartbeatSchedule.PeriodSpread), longPeriod);
    }

    /// <summary>The most beats any one <see cref="Window"/> of the mesh hub receives from the wave,
    /// counting the first beats and the first <paramref name="cycles"/> periodic beats.</summary>
    private static int MaxBeatsInOneWindow(string[] ids, double firstSpread, double periodSpread, int cycles)
    {
        var buckets = new System.Collections.Generic.Dictionary<long, int>();
        foreach (var id in ids)
        {
            var first = HeartbeatSchedule.Spread(id, FirstHeartbeat, firstSpread);
            var period = HeartbeatSchedule.Spread(id, Interval, periodSpread);
            for (var c = 0; c <= cycles; c++)
            {
                var at = first + c * period;
                var key = at.Ticks / Window.Ticks;
                buckets[key] = buckets.GetValueOrDefault(key) + 1;
            }
        }
        return buckets.Values.Max();
    }

    /// <summary>The flood shape: a wave of N mirrors opened together. With the spread, no 100 ms window
    /// of the mesh hub receives more than a small multiple of the even share, at the first beat and
    /// after several periods.</summary>
    [Fact]
    public void AWaveOfMirrors_DoesNotBeatInOneTurnWindow()
    {
        var ids = StreamIds(Wave);

        // First beats are spread over 4 s = 40 windows: even share 75. Allow 2x for hash noise.
        var firstBeats = MaxBeatsInOneWindow(ids, HeartbeatSchedule.FirstTickSpread, HeartbeatSchedule.PeriodSpread, cycles: 0);
        Assert.True(firstBeats <= 150, $"first beats of a {Wave}-mirror wave: {firstBeats} in one 100 ms window");

        // After 5 periods the phases have drifted over ~11 s per cycle: nothing may re-form a spike.
        var later = MaxBeatsInOneWindow(ids, HeartbeatSchedule.FirstTickSpread, HeartbeatSchedule.PeriodSpread, cycles: 5);
        Assert.True(later <= 150, $"beats over 5 cycles of a {Wave}-mirror wave: {later} in one 100 ms window");
    }

    /// <summary>NEGATIVE CONTROL: with no spread (the schedule before this fix) the same wave puts every
    /// mirror in a single window, which is exactly the bound the test above enforces. Proves the
    /// assertion can fail.</summary>
    [Fact]
    public void WithoutTheSpread_TheWholeWaveLandsInOneTurnWindow()
    {
        var ids = StreamIds(Wave);
        var firstBeats = MaxBeatsInOneWindow(ids, firstSpread: 0, periodSpread: 0, cycles: 0);
        Assert.Equal(Wave, firstBeats);
        var later = MaxBeatsInOneWindow(ids, firstSpread: 0, periodSpread: 0, cycles: 5);
        Assert.Equal(Wave, later);
    }

    /// <summary>The spread only ever pulls a beat EARLIER: the heartbeat keeps the owner's grain alive,
    /// so a longer cadence than configured is a regression. Also covers a short test cadence.</summary>
    [Theory]
    [InlineData(45_000)]
    [InlineData(5_000)]
    [InlineData(200)]
    public void TheSpread_IsNeverLaterThanConfigured_AndStable(int millis)
    {
        var tick = TimeSpan.FromMilliseconds(millis);
        foreach (var id in StreamIds(500))
        {
            var first = HeartbeatSchedule.Spread(id, tick, HeartbeatSchedule.FirstTickSpread);
            var period = HeartbeatSchedule.Spread(id, tick, HeartbeatSchedule.PeriodSpread);
            Assert.True(first <= tick && first >= tick * (1 - HeartbeatSchedule.FirstTickSpread) - TimeSpan.FromTicks(1));
            Assert.True(period <= tick && period >= tick * (1 - HeartbeatSchedule.PeriodSpread) - TimeSpan.FromTicks(1));
            Assert.Equal(first, HeartbeatSchedule.Spread(id, tick, HeartbeatSchedule.FirstTickSpread));
        }
    }
}
