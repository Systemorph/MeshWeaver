using System;
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

    // ───────────────── the channel the incident names: a bound that expired ─────────────────

    /// <summary>
    /// 🚨 THE CASE. A timeout is named for what it is — the bound, and that the create's fate is
    /// UNKNOWN rather than known-not-to-have-happened, which is the difference between "re-issue it"
    /// and "go and look".
    /// </summary>
    [Fact]
    public void ATimeout_NamesTheBoundItWaitedOut()
    {
        var reason = NodeTypeBuildState.Describe(new TimeoutException("timed out"), ReleasePath);
        Assert.Contains(NodeTypeBuildState.CreateBound.ToString(), reason);
        Assert.Contains(ReleasePath, reason);
        Assert.Contains("UNKNOWN", reason);
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
