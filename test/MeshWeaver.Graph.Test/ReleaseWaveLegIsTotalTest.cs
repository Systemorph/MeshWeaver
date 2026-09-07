using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3510 — one non-terminating release leg parks the whole package install, silently, for the
/// gate's entire ten-minute bound.</b>
///
/// <para><b>Measured, core CD 7976</b> (<c>19b077868</c>, bake job 101634597130 — the sixth
/// occurrence; roughly one started run in two lost its seal to this shape). The Hosting package
/// wrote its content fine: <c>Installed node-repo plugin Hosting: 144 written</c> at 05:45:28Z,
/// prebuilt adoption 15/15 at 05:45:56Z, the root recycle it is designed to survive at 05:45:56Z,
/// the deferred release wave's writes at 05:46:04Z. Then the process emitted <b>not one log line
/// and not one pending callback anywhere</b> until the installer's own 600 s bound fired at
/// 05:54:31Z — exactly <c>T+600s</c> from <c>── Hosting: installing 145 file(s)…</c>.</para>
///
/// <para><b>What that silence rules out.</b> An unanswered <c>CreateOrUpdateNodeRequest</c> — the
/// mechanism #3510 was originally attributed to — is a PENDING CALLBACK, and a pending callback is
/// reported every five seconds by <c>[STALE-CALLBACK]</c>. That reporter fired four times for
/// HomeAssistant minutes earlier in the same run and <b>zero times</b> during the eight-minute park.
/// The install was therefore not waiting on a message at all; it was parked on an observable that
/// never terminated. The control is in the same log: <b>Edu</b>, same shape, same run, printed
/// <c>[PackageInstaller] warmed installed root Edu</c> — the step that FOLLOWS the release wave —
/// 2.5 s after its own deferred wave. <c>warmed installed root Hosting</c> never printed at all, so
/// the park is inside the wave.</para>
///
/// <para><b>Why the wave and not something else in that segment.</b> Every other composition
/// between the deferred wave and the warm carries its own bound —
/// <c>AffectedNodeTypes</c> (<c>TypeEnumerationBudget</c>), <c>SeedPrebuiltAssemblies</c>
/// (<c>SeedBound</c>), and the trigger write itself (<c>BaseStateWaitBound</c> 30 s, then verdict
/// windows of 31 s across <c>MaxConflictRetries</c>). <c>RequestReleases</c>'
/// <c>nodeTypePaths.Select(ObserveNodeTypeRelease).Merge().ToList()</c> carries none, which
/// <c>MeshNodeStreamHandle.BaseStateSource</c>'s own remarks had already named in advance: "no
/// per-leg bound and no outer bound — so ONE non-terminating leg parks the entire package install,
/// silently, until the gate's own 600 s <c>InstallTimeout</c> reports <c>install:
/// TimeoutException</c> against a package that installed fine 8 minutes earlier".</para>
///
/// <para><b>What this pins.</b> Not "the leg is fast" — that would be a bound tuned to make a gate
/// pass, and this repo does not do that. It pins the CONTRACT
/// <see cref="NodeTypeReleaseExtensions.ObserveNodeTypeRelease"/> already states about itself and
/// did not keep: <i>exactly one emission, always</i>. Rx has four outcomes, not three; the closing
/// <c>DefaultIfEmpty(false)</c> covers "completed empty" and the two <c>Catch</c>es cover "faulted",
/// and nothing covered "never terminated". The first test below is the DEFECT — the wave composed
/// the way production composed it, over a leg that never answers, completing never — and the second
/// is the fix over the identical input.</para>
///
/// <para>Deterministic by construction: a <see cref="TestScheduler"/> supplies virtual time, so
/// every assertion is about ORDER and CAUSE, never about wall-clock duration. No mesh, no cluster,
/// no sleep, no hub — the shape the issue itself asked for ("extracting the inner-write
/// subscription into a pure composition … so its totality is drivable without a mesh at all").</para>
/// </summary>
public class ReleaseWaveLegIsTotalTest
{
    private const string Parked = "Hosting/InstanceRequest";
    private const string Prompt = "Hosting/LogEntry";

    /// <summary>What a wave's subscriber actually observed — all three Rx terminations, separately.</summary>
    private sealed record Observed(List<IList<bool>> Waves, Exception? Error, bool Completed);

    /// <summary>
    /// The installer's wave, verbatim: every leg subscribed at once, and the wave completes when the
    /// last of them has answered (<c>PackageInstaller.RequestReleases</c>).
    /// </summary>
    private static Observed RunWave(IEnumerable<IObservable<bool>> legs, TestScheduler scheduler, TimeSpan runFor)
    {
        var waves = new List<IList<bool>>();
        Exception? error = null;
        var completed = false;
        using var subscription = legs.Merge().ToList()
            .Subscribe(waves.Add, ex => error = ex, () => completed = true);
        scheduler.AdvanceBy(runFor.Ticks);
        return new Observed(waves, error, completed);
    }

    /// <summary>A leg that answers <c>true</c> promptly — a release whose trigger flip landed.</summary>
    private static IObservable<bool> Answers(TestScheduler scheduler, TimeSpan after) =>
        Observable.Timer(after, scheduler).Select(_ => true);

    /// <summary>
    /// 🚨 A leg that NEVER terminates: no emission, no fault, no completion. This is the fourth Rx
    /// outcome, and it is the one that reached no handler. <c>Observable.Never</c> is exactly what a
    /// permission fold or a write whose upstream stops talking looks like from here.
    /// </summary>
    private static IObservable<bool> NeverAnswers() => Observable.Never<bool>();

    /// <summary>
    /// 🚨 <b>THE DEFECT, composed the way production composed it.</b> One parked leg beside a
    /// perfectly healthy one, merged into the wave the installer waits on: the healthy leg answers
    /// in a second and the wave still never completes — not in three minutes, not in ten. That is
    /// the whole of #3510's cost: the install cannot proceed to <c>warmed installed root</c>, the
    /// gate's 600 s <c>InstallTimeout</c> is the only bound left that can fire, and it names the
    /// PACKAGE while knowing nothing about which NodeType starved.
    /// </summary>
    [Fact]
    public void UnboundedWave_WithOneLegThatNeverAnswers_NeverCompletes()
    {
        var scheduler = new TestScheduler();
        var observed = RunWave(
            [Answers(scheduler, TimeSpan.FromSeconds(1)), NeverAnswers()],
            scheduler,
            // Well past the installer's own 600 s bound — the park is unbounded, not merely long.
            runFor: TimeSpan.FromSeconds(900));

        observed.Completed.Should().BeFalse(
            "Merge().ToList() completes only when EVERY leg has terminated, so one leg that never "
            + "answers parks the whole wave — and with it the package install, for the life of the "
            + "process (#3510, core CD 7976)");
        observed.Error.Should().BeNull("nothing faulted — that is precisely why nothing was logged");
        observed.Waves.Should().BeEmpty(
            "the healthy leg answered at 1 s and its answer is stuck inside ToList's buffer: a wave "
            + "that cannot complete cannot report the legs that DID succeed either");
    }

    /// <summary>
    /// 🚨 <b>THE FIX, over the identical input.</b> The same parked leg, wrapped by
    /// <see cref="NodeTypeReleaseExtensions.BoundReleaseLeg"/>: the wave completes, the healthy leg
    /// still reports <c>true</c>, the parked one reports <c>false</c>, and the reason NAMES the
    /// NodeType — so the next occurrence is one grep instead of a full bake-log read.
    /// </summary>
    [Fact]
    public void BoundedWave_WithOneLegThatNeverAnswers_CompletesAndNamesTheLeg()
    {
        var scheduler = new TestScheduler();
        var refusals = new List<string>();
        var warnings = new List<string>();

        var observed = RunWave(
            [
                NodeTypeReleaseExtensions.BoundReleaseLeg(
                    Answers(scheduler, TimeSpan.FromSeconds(1)), Prompt,
                    NodeTypeReleaseExtensions.ReleaseRequestBound,
                    refusals.Add, warnings.Add, scheduler),
                NodeTypeReleaseExtensions.BoundReleaseLeg(
                    NeverAnswers(), Parked,
                    NodeTypeReleaseExtensions.ReleaseRequestBound,
                    refusals.Add, warnings.Add, scheduler),
            ],
            scheduler,
            // Just past the ordered inner bound — and still an order of magnitude inside the
            // installer's 600 s outer one, which is what "ordered" means.
            runFor: NodeTypeReleaseExtensions.ReleaseRequestBound + TimeSpan.FromSeconds(1));

        observed.Error.Should().BeNull(
            "a leg that cannot answer is a REFUSAL, not a fault: RequestReleases' contract is that "
            + "one unreleasable type can neither fail the install nor strand the types after it");
        observed.Completed.Should().BeTrue(
            "every leg now reaches a terminal, so the wave the installer waits on completes and the "
            + "install proceeds to WarmInstalledRoots instead of parking out the 600 s gate bound");
        var answers = observed.Waves.Should().ContainSingle().Which;
        answers.Count(a => a).Should().Be(1,
            "the healthy release still succeeded — the bound answers the parked leg, it does not "
            + "discard the wave's real results");
        answers.Count(a => !a).Should().Be(1,
            "the parked leg is answered NOT-released, which is the truth: its trigger never landed");

        refusals.Should().ContainSingle(
            "exactly one leg failed to answer, and the caller's own refusal sink is told about that "
            + "one — never about the leg that worked");
        refusals[0].Should().Contain(Parked,
            "the refusal must NAME the NodeType that starved. The outer 600 s InstallTimeout can "
            + "only say which package did not finish; this is the only bound that knows which type");
        refusals[0].Should().NotContain(Prompt);
        warnings.Should().Equal(refusals,
            "what the caller is told and what lands in the log are the same sentence — a diagnosis "
            + "that differs between the two sends the reader somewhere the other one did not");
    }

    /// <summary>
    /// The bound is ORDERED, not tightened (Doc/Architecture/BoundsMustBeOrdered): a write that is
    /// genuinely slow — every inner bound inside one trigger write composes to ≈124 s worst case —
    /// still answers on its own terms. A bound that converted a slow-but-real release into a
    /// refusal would be a regression dressed as a fix.
    /// </summary>
    [Fact]
    public void ALegThatAnswersLateButInsideTheBound_KeepsItsOwnAnswer()
    {
        var scheduler = new TestScheduler();
        var refusals = new List<string>();

        var observed = RunWave(
            [
                NodeTypeReleaseExtensions.BoundReleaseLeg(
                    // 124 s: BaseStateWaitBound (30 s) plus three verdict windows of
                    // LateResponseWatchBound + VerdictBoundGrace (31 s) across MaxConflictRetries —
                    // the worst case a LEGITIMATE trigger write can take.
                    Answers(scheduler, TimeSpan.FromSeconds(124)), Parked,
                    NodeTypeReleaseExtensions.ReleaseRequestBound,
                    refusals.Add, null, scheduler),
            ],
            scheduler,
            runFor: NodeTypeReleaseExtensions.ReleaseRequestBound + TimeSpan.FromSeconds(1));

        observed.Completed.Should().BeTrue();
        observed.Waves.Should().ContainSingle().Which.Should().Equal([true]);
        refusals.Should().BeEmpty(
            "the write answered inside its own composed bounds, so the outer ordered bound must not "
            + "fire — it exists for a leg that never answers, not for one that is slow");
    }

    /// <summary>
    /// The bound is a TOTAL deadline, not Rx's inter-emission one. A leg that keeps emitting — a
    /// re-emitting permission fold is exactly this shape, since it re-emits on every
    /// AccessAssignment change — must not be able to reset the clock forever. <c>Take(1)</c> ahead
    /// of the <c>Timeout</c> is what delivers that; the same trap <c>BaseStateSource</c> was
    /// rewritten to remove.
    /// </summary>
    [Fact]
    public void AChattyLegIsAnsweredByItsFirstEmission_NotByTheBound()
    {
        var scheduler = new TestScheduler();
        var refusals = new List<string>();

        var observed = RunWave(
            [
                NodeTypeReleaseExtensions.BoundReleaseLeg(
                    Observable.Interval(TimeSpan.FromSeconds(10), scheduler).Select(_ => true),
                    Parked,
                    NodeTypeReleaseExtensions.ReleaseRequestBound,
                    refusals.Add, null, scheduler),
            ],
            scheduler,
            runFor: NodeTypeReleaseExtensions.ReleaseRequestBound + TimeSpan.FromSeconds(1));

        observed.Completed.Should().BeTrue();
        observed.Waves.Should().ContainSingle().Which.Should().Equal([true],
            "the FIRST answer is the leg's answer — a source that keeps talking afterwards is not "
            + "an unanswered leg, and must not be treated as one");
        refusals.Should().BeEmpty();
    }
}
