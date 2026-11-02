using System;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The <c>[POOLSTALL]</c> decision and line (MeshWeaver#5388), over the readings of the 2026-11-02
/// memex-cloud stall: 69 pool threads, <c>poolCompleted(+0)</c> with 20,335 queued, between ticks that
/// were otherwise ordinary.
/// </summary>
public class ThreadPoolStallTest
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    private static ProcessLivenessSample Sample(long tick, double elapsedSeconds, long pending, long completed) =>
        new(tick, TimeSpan.FromSeconds(elapsedSeconds), 937, 382, 129, TimeSpan.FromSeconds(34.87),
            3_540_000_000L, true, 69, pending, completed);

    /// <summary>Tick 1099 of the incident: nothing completed, 20,357 queued.</summary>
    private static ProcessLivenessReading Stalled(double atSeconds = 20) =>
        ProcessLiveness.Read(
            Sample(1098, atSeconds - 10, 20_335, 43_739_919),
            Sample(1099, atSeconds, 20_357, 43_739_919),
            Period);

    [Fact]
    public void NoCompletionWithWorkQueued_IsAStall()
        => ThreadPoolStall.MadeNoProgress(Stalled()).Should().BeTrue();

    /// <summary>Controls: an IDLE pool (nothing queued) and a BUSY one (completions climbing) are not stalls.</summary>
    [Fact]
    public void AnIdlePoolAndABusyPool_AreNotStalls()
    {
        ThreadPoolStall.MadeNoProgress(ProcessLiveness.Read(
                Sample(1, 0, 0, 100), Sample(2, 10, 0, 100), Period))
            .Should().BeFalse("nothing was queued, so nothing was waiting");
        ThreadPoolStall.MadeNoProgress(ProcessLiveness.Read(
                Sample(1, 0, 1_236, 43_739_919), Sample(2, 10, 1_260, 43_789_010), Period))
            .Should().BeFalse("the pool completed work in the period");
        ThreadPoolStall.MadeNoProgress(ProcessLiveness.Read(null, Sample(1, 0, 5_000, 100), Period))
            .Should().BeFalse("the first tick has no period to judge");
    }

    [Fact]
    public void TheCaptureIsTakenOnce_ThenNotAgainInsideTheCooldown()
    {
        ThreadPoolStall.ShouldCapture(Stalled(), lastCapture: null).Should().BeTrue();
        ThreadPoolStall.ShouldCapture(Stalled(atSeconds: 40), lastCapture: TimeSpan.FromSeconds(20))
            .Should().BeFalse("a stall that persists is one stall");
        ThreadPoolStall.ShouldCapture(
                Stalled(atSeconds: 20 + ThreadPoolStall.CaptureCooldown.TotalSeconds), TimeSpan.FromSeconds(20))
            .Should().BeTrue("a stall after the cooldown is a new reading");
    }

    [Fact]
    public void TheLine_GroupsTheWorkers_LargestFirst_AndNamesTheFrame()
    {
        string Stack(string top) =>
            string.Join(Environment.NewLine, $"at {top}", "at Some.Caller()", "at System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()");
        var stacks = new[]
        {
            Stack("Held.Here()"), Stack("Held.Here()"), Stack("Held.Here()"), Stack("Elsewhere.Running()"),
        };

        var line = ThreadPoolStall.Describe(Stalled(), stacks, null);

        line.Should().StartWith("[POOLSTALL] tick=1099");
        line.Should().Contain("20357 queued over 69 thread(s)");
        line.Should().Contain("4 worker stack(s) in 2 distinct shape(s)");
        line.IndexOf("×3:", StringComparison.Ordinal).Should()
            .BeLessThan(line.IndexOf("×1:", StringComparison.Ordinal), "the largest group is printed first");
        line.Should().Contain("at Held.Here()");
    }

    [Fact]
    public void WithoutStacks_TheLineStillNamesTheStall_AndSaysWhyItCannotNameTheFrame()
    {
        ThreadPoolStall.Describe(Stalled(), null, null)
            .Should().Contain("no stack reader is registered in this host");
        ThreadPoolStall.Describe(Stalled(), null, "InvalidOperationException: no DAC")
            .Should().Contain("could NOT be read (InvalidOperationException: no DAC)");
        ThreadPoolStall.Describe(Stalled(), [], null)
            .Should().Contain("found NO ThreadPool worker thread");
    }
}
