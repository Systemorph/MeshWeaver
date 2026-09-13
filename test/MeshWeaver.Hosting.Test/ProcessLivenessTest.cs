using System;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The liveness reading, derived over fabricated samples — every shape a reader has to tell apart
/// when a log goes quiet (MeshWeaver#4234). The 2026-09-13 Education artifact is the worked example:
/// a 13.64&#160;s GC pause that the process DID resume from (and Orleans reported), against a 90&#160;s
/// silence it never resumed from (and nothing reported).
/// </summary>
public class ProcessLivenessTest
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    private static ProcessLivenessSample Sample(
        long tick,
        double elapsedSeconds,
        int gen0 = 450, int gen1 = 421, int gen2 = 21,
        double gcPauseSeconds = 20.0,
        long heapBytes = 6_240_000_000L,
        bool serverGc = false,
        int poolThreads = 55,
        long poolPending = 0,
        long poolCompleted = 1_000_000) =>
        new(tick, TimeSpan.FromSeconds(elapsedSeconds), gen0, gen1, gen2,
            TimeSpan.FromSeconds(gcPauseSeconds), heapBytes, serverGc, poolThreads, poolPending, poolCompleted);

    [Fact]
    public void TheFirstTick_HasNoGapAndNoDeltas_AndSaysSo()
    {
        var reading = ProcessLiveness.Read(null, Sample(1, 10), Period);

        reading.Gap.Should().BeNull();
        reading.Gen2Delta.Should().BeNull();
        reading.Overran.Should().BeFalse();
        reading.Describe().Should().Contain("gap=first");
    }

    /// <summary>
    /// 🚨 The POSITIVE CONTROL for every "does not say OVERRAN" assertion below: an ordinary tick
    /// still publishes every field, because a heartbeat that prints only when it is unhappy makes
    /// "nothing happened" and "I was not running" the same bytes.
    /// </summary>
    [Fact]
    public void AnOrdinaryTick_PublishesEveryField_AndDoesNotClaimAnOverrun()
    {
        var line = ProcessLiveness.Read(
            Sample(7, 70, gen0: 450, gen1: 421, gen2: 21, gcPauseSeconds: 20.0, poolCompleted: 1_000_000),
            Sample(8, 80.01, gen0: 453, gen1: 422, gen2: 21, gcPauseSeconds: 20.004, poolCompleted: 1_004_321),
            Period).Describe();

        line.Should().Contain("tick=8");
        line.Should().Contain("gap=10.01s/10.00s");
        line.Should().Contain("gen0=453(+3)");
        line.Should().Contain("gen1=422(+1)");
        line.Should().Contain("gen2=21(+0)");
        line.Should().Contain("gcPause=20.00s(+0.00s)");
        line.Should().Contain("heap=5.81GiB");
        line.Should().Contain("serverGC=False");
        line.Should().Contain("poolThreads=55");
        line.Should().Contain("poolPending=0");
        line.Should().Contain("poolCompleted=1004321(+4321)");
        line.Should().NotContain("OVERRAN");
    }

    /// <summary>
    /// The 2026-09-13 08:24:19Z shape: Orleans reported <c>Platform stalled for 00:00:13.70. Total
    /// GC Pause duration during that period: 00:00:13.64</c>. The heartbeat says the same thing from
    /// inside the process, without needing Orleans to be hosted — and it says it as a tick that came
    /// back late, which is the only honest way to report a stop.
    /// </summary>
    [Fact]
    public void ATickThatCameBackLateAfterAGcPause_NamesTheOverrunAndTheGcShare()
    {
        var reading = ProcessLiveness.Read(
            Sample(9, 90, gcPauseSeconds: 20.0),
            Sample(10, 113.70, gcPauseSeconds: 33.64),
            Period);

        reading.Overran.Should().BeTrue();
        var line = reading.Describe();
        line.Should().Contain("gap=23.70s/10.00s");
        line.Should().Contain("OVERRAN by 13.70s");
        line.Should().Contain("13.64s was GC pause");
        line.Should().Contain("100% of the overrun");
    }

    /// <summary>
    /// 🚨 The NEGATIVE half, so the case above can never go vacuous: OVERRAN alone must not read as
    /// "GC". A stop with no GC in it is a different owner entirely, and the line has to say which.
    /// </summary>
    [Fact]
    public void ATickThatCameBackLateWithNoGcInTheGap_SaysTheOverrunWasNotGc()
    {
        var line = ProcessLiveness.Read(
            Sample(9, 90, gcPauseSeconds: 20.0),
            Sample(10, 113.70, gcPauseSeconds: 20.0),
            Period).Describe();

        line.Should().Contain("OVERRAN by 13.70s");
        line.Should().Contain("0.00s was GC pause");
        line.Should().Contain("0% of the overrun");
    }

    /// <summary>
    /// Thread-pool starvation, which a CPU sample cannot see either: the pool is queueing work and
    /// finishing none. The heartbeat keeps ticking because it is NOT on the pool — that is the whole
    /// reason it runs on a thread of its own.
    /// </summary>
    [Fact]
    public void AStarvedPool_IsVisibleAsPendingClimbingWhileCompletedIsFlat()
    {
        var line = ProcessLiveness.Read(
            Sample(11, 110, poolPending: 0, poolCompleted: 2_000_000),
            Sample(12, 120, poolPending: 4_812, poolCompleted: 2_000_000),
            Period).Describe();

        line.Should().Contain("poolPending=4812");
        line.Should().Contain("poolCompleted=2000000(+0)");
        line.Should().NotContain("OVERRAN");
    }

    [Fact]
    public void OrdinarySchedulingJitter_IsNotReportedAsAnOverrun()
    {
        ProcessLiveness.Read(Sample(1, 10), Sample(2, 24), Period).Overran.Should().BeFalse();
        ProcessLiveness.Read(Sample(1, 10), Sample(2, 26), Period).Overran.Should().BeTrue();
    }

    [Fact]
    public void TheCadence_DefaultsWhenUnset_AndIsOffOnlyWhenExplicitlyZero()
    {
        ProcessLiveness.PeriodOf(null).Should().Be(ProcessLiveness.DefaultPeriod);
        ProcessLiveness.PeriodOf("").Should().Be(ProcessLiveness.DefaultPeriod);
        ProcessLiveness.PeriodOf("0").Should().Be(TimeSpan.Zero);
        ProcessLiveness.PeriodOf("-5").Should().Be(TimeSpan.Zero);
        ProcessLiveness.PeriodOf("2.5").Should().Be(TimeSpan.FromSeconds(2.5));
    }

    /// <summary>
    /// 🚨 A typo must not silently remove an instrument. <c>"ten"</c> falls back to the default
    /// cadence, never to off — the failure mode of the opposite choice is a portal that publishes
    /// no liveness line and nobody noticing until a wedge report has to be read.
    /// </summary>
    [Fact]
    public void AMalformedCadence_FallsBackToTheDefault_NeverToOff()
    {
        ProcessLiveness.PeriodOf("ten").Should().Be(ProcessLiveness.DefaultPeriod);
        ProcessLiveness.PeriodOf("10s").Should().Be(ProcessLiveness.DefaultPeriod);
    }

    /// <summary>
    /// 🚨 These four PARSE. <c>double.TryParse</c> accepts them and <c>TimeSpan.FromSeconds</c>
    /// throws on every one — the throw is caught at the call site, so the host boots and the
    /// heartbeat simply never runs, which is precisely the silent removal the case above forbids.
    /// A value that is not a finite number, or one past <see cref="ProcessLiveness.MaximumPeriod"/>,
    /// is a malformed value and lands on the default like any other.
    /// </summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    [InlineData("1e30")]
    public void ANonFiniteOrAbsurdCadence_FallsBackToTheDefault_AndNeverThrows(string configured)
    {
        ProcessLiveness.PeriodOf(configured).Should().Be(ProcessLiveness.DefaultPeriod);
    }

    /// <summary>
    /// 🚨 The negative control for the case above, so the bound cannot silently swallow a legitimate
    /// long cadence: a value INSIDE <see cref="ProcessLiveness.MaximumPeriod"/> is honoured.
    /// </summary>
    [Fact]
    public void ALongButFiniteCadence_IsHonoured()
    {
        ProcessLiveness.PeriodOf("3600").Should().Be(TimeSpan.FromHours(1));
        ProcessLiveness.PeriodOf("86400").Should().Be(ProcessLiveness.MaximumPeriod);
    }
}
