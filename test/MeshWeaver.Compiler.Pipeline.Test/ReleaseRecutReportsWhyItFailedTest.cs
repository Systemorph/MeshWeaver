using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #5057 — a remedy that reports its own failure must be able to report the FAILURE.
///
/// <para><c>ReleasePostCondition</c> re-cuts the release a consumed request owes, and logs an ERROR
/// when the re-cut does not land. In production that error read, in full: <i>"…AND the release could
/// not be re-cut. The node advertises a build no release names; instances will keep binding 'X' until
/// a release is created for it."</i> — eight occurrences over three minutes across seven node types,
/// naming the consequence, the stale path and the build, and not once the cause. The cause was
/// discarded one frame below, where <c>TryCreateReleaseNode</c> collapsed four distinct failures into
/// a bare <c>null</c>; and the channel the incident actually names wrote nothing at all, because
/// <c>Timeout(bound, Observable.Return&lt;string?&gt;(null))</c> SUBSTITUTES rather than faults. The
/// evidence is a ten-second gap: on <c>Hosting/InstanceRequest</c> the "Re-cutting…" line was logged
/// at 22:16:14Z and "…could not be re-cut" at 22:16:24Z — exactly
/// <see cref="NodeTypeBuildState.CreateBound"/>, expiring, reported by nothing but the fact that the
/// two lines happened to be adjacent.</para>
///
/// <para><b>The control on each side.</b> Every failure channel must carry a reason that reaches the
/// operator-facing sentence (the cases below would have been unwritable against the old shape — there
/// was no reason to assert on); and a create that LANDED must still read as a clean success, with no
/// failure wording anywhere near it. The pure verdict <c>Violation</c> and the collision adoption are
/// pinned by <c>ReleasePostConditionTest</c> and <c>ReleaseRecutAdoptsItsOwnCollisionTest</c>; the
/// end-to-end settle is pinned on a real mesh by <c>ReleasePostConditionAtSettleTest</c> in
/// MeshWeaver.Plugins.</para>
/// </summary>
public class ReleaseRecutReportsWhyItFailedTest
{
    private const string ReleasePath = "Hosting/InstanceRequest/Release/20260920221436-CNCf6e7F";
    private const string Violation =
        "a release request was consumed and this compile succeeded, yet latestReleasePath still "
        + "names an EARLIER build";

    // ───────────── the PRODUCTION CHAIN, driven by a clock (review on #5057) ─────────────
    //
    // 🚨 The cases below exist because the ones further down do NOT cover the load-bearing change.
    // `Timeout(CreateBound)` faulting instead of SUBSTITUTING is the whole fix, and a test that hands
    // a hand-built TimeoutException to `Describe` would stay green through a revert to
    // `Timeout(CreateBound, Observable.Return(<an outcome with no reason>))`. These drive
    // `Bounded` — the real `.Timeout(…).Catch(…)` chain — on a HistoricalScheduler, so the expiry is
    // the scheduler's, not a sleep's, and the substituting revert is caught by the outcome arriving
    // with nothing to say.

    /// <summary>Runs <paramref name="drive"/> against the production chain and returns its one outcome.</summary>
    private static NodeTypeBuildState.ReleaseCreateOutcome? Run(
        Action<Subject<Unit>, HistoricalScheduler> drive)
    {
        var create = new Subject<Unit>();
        var clock = new HistoricalScheduler();
        NodeTypeBuildState.ReleaseCreateOutcome? seen = null;
        using var subscription = NodeTypeBuildState
            .Bounded(create, ReleasePath, clock, logger: null)
            .Subscribe(outcome => seen = outcome, _ => { });
        drive(create, clock);
        return seen;
    }

    /// <summary>
    /// 🚨 THE REVERT DETECTOR. The clock passes the bound with no answer from the create, and the
    /// outcome must carry a REASON naming the bound. A substituting fallback emits an outcome with no
    /// reason at all — `Because` then reports that the attempt said nothing — so this assertion is
    /// what a revert to <c>Timeout(bound, other)</c> cannot satisfy.
    /// </summary>
    [Fact]
    public void AnElapsedCreate_ReportsTheBound_ThroughTheRealChain()
    {
        var outcome = Run((_, clock) => clock.AdvanceBy(NodeTypeBuildState.CreateBound.Add(TimeSpan.FromTicks(1))));

        Assert.NotNull(outcome);
        Assert.False(outcome!.Succeeded);
        Assert.True(outcome.Attempted);
        Assert.NotNull(outcome.Failure);
        Assert.Contains(NodeTypeBuildState.CreateBound.ToString(), outcome.Failure!);
        Assert.DoesNotContain("reported no reason", outcome.Because);
    }

    /// <summary>
    /// The other side: a create that answers INSIDE the bound is a plain success, with no failure
    /// wording and the path intact. A fix that reported every outcome as a failure would satisfy the
    /// case above and be worse than the defect.
    /// </summary>
    [Fact]
    public void ACreateThatLandsInsideTheBound_Succeeds_ThroughTheRealChain()
    {
        var outcome = Run((create, clock) =>
        {
            clock.AdvanceBy(NodeTypeBuildState.CreateBound - TimeSpan.FromSeconds(1));
            create.OnNext(Unit.Default);
        });

        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded);
        Assert.Equal(ReleasePath, outcome.ReleasePath);
        Assert.Null(outcome.Failure);
    }

    /// <summary>
    /// 🚨 #3407 through the chain rather than only through <c>AdoptOnOwnCollision</c> in isolation: a
    /// create refused because the path is already taken has SUCCEEDED, so the pointer advances.
    /// </summary>
    [Fact]
    public void AnAlreadyExistsRefusal_IsAdopted_ThroughTheRealChain()
    {
        var outcome = Run((create, _) => create.OnError(
            CreateNodeResponse.Fail("taken", NodeCreationRejectionReason.NodeAlreadyExists)
                .ToException(ReleasePath)));

        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded);
        Assert.Equal(ReleasePath, outcome.ReleasePath);
    }

    /// <summary>
    /// Any other refusal leaves the pointer un-advanced AND says why — advertising a release path
    /// whose node does not exist would be worse than the bug being fixed.
    /// </summary>
    [Fact]
    public void AnyOtherRefusal_FailsWithItsReason_ThroughTheRealChain()
    {
        var outcome = Run((create, _) => create.OnError(
            new InvalidOperationException("cross-hub write PARTIALLY refused")));

        Assert.NotNull(outcome);
        Assert.False(outcome!.Succeeded);
        Assert.Contains("cross-hub write PARTIALLY refused", outcome.Failure!);
    }

    // ───────────────── the channel the incident names: a bound that expired ─────────────────

    /// <summary>
    /// 🚨 THE CASE. A timeout is named for what it is — the bound, and that the create's fate is
    /// UNKNOWN rather than known-not-to-have-happened, which is the difference between "re-issue it"
    /// and "go and look".
    ///
    /// <para>Measured on the control instance over <c>Hosting/InstanceRequest/Release/*</c> (200
    /// nodes, a floor — the listing truncated): 8 of 200 creates landed AFTER this bound, out to
    /// 17.8 s, against a median of 0.7 s. The bound stops the WAIT, not the create, so the sentence
    /// must send the reader to the path rather than let them conclude the release is missing.</para>
    /// </summary>
    [Fact]
    public void ATimeout_NamesTheBoundAndSendsTheReaderToThePath()
    {
        var reason = NodeTypeBuildState.Describe(new TimeoutException("timed out"), ReleasePath);
        Assert.Contains(NodeTypeBuildState.CreateBound.ToString(), reason);
        Assert.Contains(ReleasePath, reason);
        // Not "was not created": the create outlives the wait, so the node may well exist.
        Assert.Contains("may well exist", reason);
        Assert.DoesNotContain("was not created", reason);
    }

    /// <summary>Any other fault names its exception type and message — never a bare class name.</summary>
    [Fact]
    public void ARefusal_NamesTheExceptionAndItsMessage()
    {
        var reason = NodeTypeBuildState.Describe(
            new InvalidOperationException("cross-hub write PARTIALLY refused"), ReleasePath);
        Assert.Contains(nameof(InvalidOperationException), reason);
        Assert.Contains("cross-hub write PARTIALLY refused", reason);
        Assert.Contains(ReleasePath, reason);
    }

    // ───────────────── the three states a string? could hold only two of ─────────────────

    /// <summary>A landed create: a path, no reason, and it reads as success.</summary>
    [Fact]
    public void ALandedCreate_Succeeds_AndCarriesNoReason()
    {
        var landed = NodeTypeBuildState.ReleaseCreateOutcome.Landed(ReleasePath);
        Assert.True(landed.Succeeded);
        Assert.Null(landed.Failure);
        Assert.Equal(ReleasePath, landed.ReleasePath);
    }

    /// <summary>A failed create: no path, and the reason survives into the appendable clause.</summary>
    [Fact]
    public void AFailedCreate_CarriesItsReason()
    {
        var failed = NodeTypeBuildState.ReleaseCreateOutcome.Failed("the owning hub did not answer");
        Assert.False(failed.Succeeded);
        Assert.Contains("the owning hub did not answer", failed.Because);
    }

    /// <summary>
    /// 🚨 NOT-ATTEMPTED is not FAILED. A compile that produced no assembly was never asked to release
    /// anything, and a report that calls that a failure sends the reader after a create nobody made.
    /// </summary>
    [Fact]
    public void ANotAttemptedCreate_IsDistinguishableFromAFailedOne()
    {
        var none = NodeTypeBuildState.ReleaseCreateOutcome.NotAttempted;
        Assert.False(none.Attempted);
        Assert.False(none.Succeeded);
        Assert.Null(none.Failure);
        Assert.Contains("no create was attempted", none.Because);
        Assert.DoesNotContain("no create was attempted",
            NodeTypeBuildState.ReleaseCreateOutcome.Failed("refused").Because);
    }

    /// <summary>
    /// 🚨 A REPORT THAT CANNOT FAIL IS NOT A REPORT. An attempted failure that arrived with no reason
    /// must SAY the reason is missing, not trail off — otherwise the exact defect #5057 is would
    /// reappear as an empty clause and read as terseness.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnAttemptedFailureWithNoReason_SaysSoRatherThanTrailingOff(string? reason)
    {
        var mute = new NodeTypeBuildState.ReleaseCreateOutcome(null, reason, Attempted: true);
        Assert.NotEqual("", mute.Because);
        Assert.Contains("defect", mute.Because);
    }

    // ───────────────── the operator-facing sentences ─────────────────

    /// <summary>
    /// 🚨 THE LINE THE INCIDENT WAS FILED FROM. Both causes reach it: the settle's own create (which
    /// used to be a <c>Warning</c> no operator-facing surface carried) and the re-cut.
    /// </summary>
    [Fact]
    public void TheFailedDiagnosis_NamesBothCauses()
    {
        var sentence = ReleasePostCondition.FailedDiagnosis(
            Violation,
            ReleasePostCondition.FirstAttemptClause(
                NodeTypeBuildState.ReleaseCreateOutcome.Failed("the attributed create was refused")),
            NodeTypeBuildState.ReleaseCreateOutcome.Failed(
                NodeTypeBuildState.Describe(new TimeoutException("nope"), ReleasePath)));

        Assert.Contains(Violation, sentence);
        Assert.Contains("the attributed create was refused", sentence);
        Assert.Contains(NodeTypeBuildState.CreateBound.ToString(), sentence);
        Assert.Contains("This build has no release.", sentence);
    }

    /// <summary>
    /// The OTHER side of the control: a re-cut that WORKED must read as a repair, naming where the
    /// release landed, with no "could not" anywhere in it. A fix that made every outcome sound like a
    /// failure would pass every assertion above and be worse than the defect.
    /// </summary>
    [Fact]
    public void TheRestoredDiagnosis_ReadsAsARepair()
    {
        var sentence = ReleasePostCondition.RestoredDiagnosis(
            Violation,
            ReleasePostCondition.FirstAttemptClause(
                NodeTypeBuildState.ReleaseCreateOutcome.Failed("the attributed create was refused")),
            ReleasePath);

        Assert.Contains($"Restored at {ReleasePath}", sentence);
        Assert.Contains("no recompile", sentence);
        Assert.DoesNotContain("VIOLATED", sentence);
        Assert.DoesNotContain("has no release", sentence);
    }

    // ───────────────── the transcript entry is KEYED, or it is English forever ─────────────────

    /// <summary>
    /// 🚨 Both activity entries carry a catalog key and their arguments, so a German viewer reads a
    /// German sentence (<c>LogMessage</c>, #3236). The English text stays as the FALLBACK — that is
    /// what keeps an old persisted row, and a key that later leaves the catalog, rendering as they do
    /// today. Without this case the keying could be dropped and every sentence assertion above would
    /// stay green while the transcript silently went English-only again.
    /// </summary>
    [Fact]
    public void BothActivityEntries_AreKeyed_WithTheEnglishTextAsFallback()
    {
        var clause = ReleasePostCondition.FirstAttemptClause(
            NodeTypeBuildState.ReleaseCreateOutcome.Failed("refused"));

        var restored = ReleasePostCondition.RestoredEntry(Violation, clause, ReleasePath);
        Assert.Equal(ReleasePostCondition.RestoredKey, restored.MessageKey);
        Assert.Equal(
            ReleasePostCondition.RestoredDiagnosis(Violation, clause, ReleasePath), restored.Message);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, restored.LogLevel);

        var recut = NodeTypeBuildState.ReleaseCreateOutcome.Failed("the owning hub did not answer");
        var violated = ReleasePostCondition.ViolatedEntry(Violation, clause, recut);
        Assert.Equal(ReleasePostCondition.ViolatedKey, violated.MessageKey);
        Assert.Equal(
            ReleasePostCondition.FailedDiagnosis(Violation, clause, recut), violated.Message);
        // Error, not Warning: this build has no release.
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, violated.LogLevel);

        // 🚨 Every placeholder the catalog entries spell is supplied, or a German render shows the
        // literal token. These are the ONLY names the two catalog values use.
        Assert.NotNull(restored.MessageArgs);
        Assert.Equal(["firstAttempt", "path", "violation"], restored.MessageArgs!.Keys.Order());
        Assert.NotNull(violated.MessageArgs);
        Assert.Equal(["firstAttempt", "reason", "violation"], violated.MessageArgs!.Keys.Order());
    }

    /// <summary>A settle that attempted nothing says exactly that, in both sentences.</summary>
    [Fact]
    public void ANotAttemptedFirstCreate_IsWordedAsSuch()
        => Assert.Contains(
            "No create was attempted",
            ReleasePostCondition.FirstAttemptClause(
                NodeTypeBuildState.ReleaseCreateOutcome.NotAttempted));

    /// <summary>
    /// And a first create that LANDED is worded as a landing — the clause is total, so a future
    /// caller that reaches it with a success cannot produce a sentence claiming a failure.
    /// </summary>
    [Fact]
    public void ALandedFirstCreate_IsWordedAsALanding()
    {
        var clause = ReleasePostCondition.FirstAttemptClause(
            NodeTypeBuildState.ReleaseCreateOutcome.Landed(ReleasePath));
        Assert.Contains($"landed at {ReleasePath}", clause);
        Assert.DoesNotContain("defect", clause);
    }
}
