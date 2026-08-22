using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// The shutdown narrative a terminating pod writes into its own log.
///
/// <para>The thing under test is a DIAGNOSTIC, so the failure directions are not symmetric. Too
/// few lines and the pod is opaque again — which is the state #1794 was filed about, where a pod
/// held a node for twenty-nine minutes and nobody could tell from outside whether that was a
/// forgotten browser tab or a wedge. Too many and a full 1800 s drain puts 360 Information lines
/// per pod termination into Loki for no extra information. Both are pinned below.</para>
///
/// <para>Everything runs on a supplied clock: the drain being modelled is half an hour long, and a
/// test that actually waited for it would be useless.</para>
/// </summary>
public class DrainProgressTest
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 8, 40, 52, TimeSpan.Zero);

    /// <summary>
    /// The line that makes every later line readable: after it, any Critical in this process came
    /// from a replica Kubernetes had already deleted. Nothing else in the log says so, because
    /// preStop runs before SIGTERM and no lifetime event has fired.
    /// </summary>
    [Fact]
    public void FirstProbeWithSessionsOpen_AnnouncesTermination()
    {
        var progress = new DrainProgress();

        progress.TerminationBegun.Should().BeFalse("nothing has probed /drain yet");

        var report = progress.Probe(5, T0);

        report.Outcome.Should().Be(DrainProbeOutcome.TerminationBegun);
        report.LiveCircuits.Should().Be(5);
        report.CircuitsWhenTerminationBegan.Should().Be(5);
        report.Elapsed.Should().Be(TimeSpan.Zero);
        report.ProbeCount.Should().Be(1);
        progress.TerminationBegun.Should().BeTrue("a /drain probe is the only notice this pod gets");
    }

    /// <summary>The healthy rollout — and worth exactly one line, so that a SLOW one stands out.</summary>
    [Fact]
    public void FirstProbeWithNobodyConnected_ReportsDrainedImmediately()
    {
        var progress = new DrainProgress();

        var report = progress.Probe(0, T0);

        report.Outcome.Should().Be(DrainProbeOutcome.Drained);
        report.Elapsed.Should().Be(TimeSpan.Zero);
    }

    /// <summary>
    /// preStop probes every 5 s. At the 1800 s ceiling that is 360 probes, and reporting each one
    /// would make the fix more expensive than the opacity it removes.
    /// </summary>
    [Fact]
    public void ProbesInsideTheInterval_AreSilent()
    {
        var progress = new DrainProgress();
        progress.Probe(5, T0).Outcome.Should().Be(DrainProbeOutcome.TerminationBegun);

        for (var seconds = 5; seconds < 60; seconds += 5)
            progress.Probe(5, T0.AddSeconds(seconds)).Outcome.Should()
                .Be(DrainProbeOutcome.Silent, $"the probe at +{seconds}s is inside the report interval");
    }

    /// <summary>
    /// A full 1800 s drain that never progresses must stay bounded. 360 probes in, the log holds
    /// the opening line plus one per minute — about thirty lines, not three hundred and sixty.
    /// </summary>
    [Fact]
    public void AFullGracePeriodOfProbes_StaysBounded()
    {
        var progress = new DrainProgress();
        var reported = 0;

        for (var seconds = 0; seconds < 1800; seconds += 5)
            if (progress.Probe(5, T0.AddSeconds(seconds)).Outcome != DrainProbeOutcome.Silent)
                reported++;

        reported.Should().Be(30, "the opening line plus one per minute of a 30-minute drain");
    }

    /// <summary>
    /// The distinguishing signal. A drain that is progressing ends by itself; a flat one rides the
    /// ceiling to SIGKILL, and the log has to SAY which of the two is happening — that is the whole
    /// question #1794 could not answer from outside.
    /// </summary>
    [Fact]
    public void FallingCount_ReportsProgress_FlatCountDoesNot()
    {
        var progress = new DrainProgress();
        progress.Probe(5, T0);

        var falling = progress.Probe(3, T0.AddSeconds(60));
        falling.Outcome.Should().Be(DrainProbeOutcome.StillDraining);
        falling.CircuitsAtLastReport.Should().Be(5, "the previous line said 5");
        falling.Progressing.Should().BeTrue("5 → 3 is a drain that will end on its own");

        var flat = progress.Probe(3, T0.AddSeconds(120));
        flat.Outcome.Should().Be(DrainProbeOutcome.StillDraining);
        flat.CircuitsAtLastReport.Should().Be(3);
        flat.Progressing.Should().BeFalse("3 → 3 is the forgotten tab that will be SIGKILLed");
        flat.CircuitsWhenTerminationBegan.Should().Be(5, "every line carries where the drain started");
        flat.Elapsed.Should().Be(TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// A count that goes UP is not progress. Someone can still open a session against a terminating
    /// pod through an affinity cookie, and calling that "progressing" would read as an ending in
    /// sight when the opposite is true.
    /// </summary>
    [Fact]
    public void RisingCount_IsNotProgress()
    {
        var progress = new DrainProgress();
        progress.Probe(2, T0);

        var rising = progress.Probe(4, T0.AddSeconds(60));

        rising.Outcome.Should().Be(DrainProbeOutcome.StillDraining);
        rising.Progressing.Should().BeFalse("2 → 4 is further from done, not closer");
    }

    /// <summary>
    /// The ending, and only one of them. preStop exits on the first success, so a second "drained"
    /// line means something else is polling the endpoint — count it, say nothing.
    /// </summary>
    [Fact]
    public void ReachingZero_ReportsDrainedExactlyOnce()
    {
        var progress = new DrainProgress();
        progress.Probe(5, T0);

        var drained = progress.Probe(0, T0.AddSeconds(30));

        drained.Outcome.Should().Be(DrainProbeOutcome.Drained,
            "the ending is reported the moment it happens, not at the next interval");
        drained.Elapsed.Should().Be(TimeSpan.FromSeconds(30));
        drained.CircuitsWhenTerminationBegan.Should().Be(5);

        progress.Probe(0, T0.AddSeconds(35)).Outcome.Should().Be(DrainProbeOutcome.Silent);
        progress.Probe(0, T0.AddMinutes(10)).Outcome.Should().Be(DrainProbeOutcome.Silent,
            "not even a later interval boundary reopens a finished narrative");
    }

    /// <summary>
    /// <see cref="ActiveCircuitTracker"/> clamps at zero, but this type must not depend on that:
    /// a negative count read as "still busy" would swallow the Drained line, and the log would end
    /// mid-sentence on exactly the rollout that went fine.
    /// </summary>
    [Fact]
    public void NegativeCount_ReadsAsDrained()
    {
        var progress = new DrainProgress();
        progress.Probe(1, T0);

        var report = progress.Probe(-3, T0.AddSeconds(10));

        report.Outcome.Should().Be(DrainProbeOutcome.Drained);
        report.LiveCircuits.Should().Be(0, "a negative count is reported as none, never as -3");
    }

    /// <summary>
    /// A clock that steps backwards (NTP correction mid-drain) must not throw and must not spray
    /// lines — it simply does not reach the next interval yet.
    /// </summary>
    [Fact]
    public void ClockGoingBackwards_StaysSilent()
    {
        var progress = new DrainProgress();
        progress.Probe(5, T0.AddMinutes(5));

        var report = progress.Probe(5, T0);

        report.Outcome.Should().Be(DrainProbeOutcome.Silent);
    }

    /// <summary>
    /// <c>/drain</c> is an anonymous HTTP endpoint, so probes can arrive on any thread and in
    /// parallel. Exactly one of them may announce the start of termination — two opening lines
    /// would put two different "began" timestamps in the log and make the elapsed times in every
    /// later line unreadable.
    /// </summary>
    [Fact]
    public void ConcurrentProbes_AnnounceTerminationExactlyOnce()
    {
        var progress = new DrainProgress();
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<DrainProbeOutcome>();

        Parallel.For(0, 64, i => outcomes.Add(progress.Probe(5, T0.AddMilliseconds(i)).Outcome));

        outcomes.Count(o => o == DrainProbeOutcome.TerminationBegun).Should().Be(1);
        outcomes.Count(o => o == DrainProbeOutcome.Silent).Should().Be(63,
            "every other probe fell inside the report interval");
    }

    /// <summary>Every probe is counted even when it is not reported — the line says how many.</summary>
    [Fact]
    public void EveryProbeIsCounted_EvenTheSilentOnes()
    {
        var progress = new DrainProgress();

        for (var seconds = 0; seconds < 60; seconds += 5)
            progress.Probe(5, T0.AddSeconds(seconds));

        var report = progress.Probe(5, T0.AddSeconds(60));

        report.Outcome.Should().Be(DrainProbeOutcome.StillDraining);
        report.ProbeCount.Should().Be(13, "twelve silent probes plus this one");
    }

    // ── what SIGTERM found (#1971) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The line whose EXISTENCE is the point. Before preStop was bounded, a pod whose sessions
    /// outlived the grace was SIGKILLed with a live Orleans silo — so nothing ran at shutdown, and
    /// "did this pod depart membership cleanly?" was unanswerable from Loki in either direction.
    /// A drain that gives up at its deadline reports the sessions it is cutting off, by count.
    /// </summary>
    [Fact]
    public void GivingUpAtTheDeadline_ReportsTheSessionsItCutsOff()
    {
        var progress = new DrainProgress();
        progress.Probe(5, T0);
        progress.Probe(3, T0.AddSeconds(600));

        var report = progress.Abandon(3, T0.AddSeconds(1680));

        report.CutSessionsOff.Should().BeTrue();
        report.LiveCircuits.Should().Be(3);
        report.CircuitsWhenTerminationBegan.Should().Be(5);
        report.Elapsed.Should().Be(TimeSpan.FromSeconds(1680));
        report.TerminationWasObserved.Should().BeTrue();
    }

    /// <summary>
    /// The ordinary roll: everyone finished, then SIGTERM. It must NOT read as sessions being cut
    /// off — a warning on every clean roll is a warning nobody reads on the roll that matters.
    /// </summary>
    [Fact]
    public void ADrainThatFinished_ReportsNoSessionsCutOff()
    {
        var progress = new DrainProgress();
        progress.Probe(2, T0);
        progress.Probe(0, T0.AddSeconds(30));

        var report = progress.Abandon(0, T0.AddSeconds(31));

        report.CutSessionsOff.Should().BeFalse();
        report.CircuitsWhenTerminationBegan.Should().Be(2);
        report.TerminationWasObserved.Should().BeTrue();
    }

    /// <summary>
    /// SIGTERM with no preStop at all — a node eviction, a local Ctrl-C, a chart that lost its
    /// lifecycle hook. Distinguished from "the drain ran and gave up", because the two need
    /// different responses: one is a session-length problem, the other is a missing hook.
    /// </summary>
    [Fact]
    public void ShutdownWithoutAnyProbe_SaysNoDrainWasEverObserved()
    {
        var report = new DrainProgress().Abandon(4, T0);

        report.TerminationWasObserved.Should().BeFalse();
        report.LiveCircuits.Should().Be(4);
        report.CircuitsWhenTerminationBegan.Should().Be(4,
            "with no probe to compare against, the shutdown count is the only count there is");
        report.Elapsed.Should().Be(TimeSpan.Zero);
    }

    /// <summary>A clamped count must never turn into a phantom cut-off session.</summary>
    [Fact]
    public void ANegativeCountIsClampedAtShutdownToo()
    {
        var progress = new DrainProgress();
        progress.Probe(1, T0);

        progress.Abandon(-3, T0.AddSeconds(5)).CutSessionsOff.Should().BeFalse();
    }
}
