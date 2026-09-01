using System;
using System.Linq;
using Memex.Portal.Shared.SelfUpdate;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The pure half of #2553: the verdict a self-update check reports. No hub, no logger, no Rx —
/// these are the judgements the reporting site depends on, and they are worth pinning here rather
/// than re-deriving them inside an integration test that also has to stand up a mesh.
/// </summary>
public class SelfUpdateVerdictTest
{
    /// <summary>
    /// 🚨 The load-bearing discriminator. The dead-event-channel warning fires only when a
    /// safety-net check FOUND a release nobody announced, and this predicate is what "found" means.
    /// Get it wrong in the permissive direction and every quiet install warns hourly until people
    /// stop reading the line; get it wrong in the strict direction and the one report that would
    /// have named #2494 never fires.
    /// </summary>
    [Theory]
    [InlineData(SelfUpdateOutcome.Applied, true)]
    [InlineData(SelfUpdateOutcome.Held, true)]
    [InlineData(SelfUpdateOutcome.Deferred, true)]
    [InlineData(SelfUpdateOutcome.DetectOnly, true)]
    // A combo refusal is a release that WAS waiting — the dead-event-channel report must fire for
    // it exactly as it does for an availability hold: something published and nothing told us.
    [InlineData(SelfUpdateOutcome.ComboBlocked, true)]
    [InlineData(SelfUpdateOutcome.NoNewerRelease, false)]
    [InlineData(SelfUpdateOutcome.UpdatesDisabled, false)]
    [InlineData(SelfUpdateOutcome.CheckFailed, false)]
    [InlineData(SelfUpdateOutcome.NoOutcome, false)]
    public void FoundNewerRelease_IsTrueExactlyWhenAReleaseWasWaiting(
        SelfUpdateOutcome outcome, bool expected)
        => Assert.Equal(expected, new SelfUpdateVerdict(outcome, "…").FoundNewerRelease);

    /// <summary>
    /// Every outcome must be reachable through a factory. An enum member with no constructor is a
    /// state the service can never report, which is the same silence one level up.
    /// </summary>
    [Fact]
    public void EveryOutcome_HasAFactoryThatProducesIt()
    {
        SelfUpdateVerdict[] all =
        [
            SelfUpdateVerdict.UpdatesDisabled(),
            SelfUpdateVerdict.NoNewerRelease(7, "3.0.0"),
            SelfUpdateVerdict.Held("3.0.1", "no sealed bake"),
            SelfUpdateVerdict.Deferred("3.0.1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)),
            SelfUpdateVerdict.DetectOnly("3.0.1"),
            SelfUpdateVerdict.Applied("3.0.1", "3.0.0", null),
            SelfUpdateVerdict.CheckFailed(new InvalidOperationException("boom")),
            SelfUpdateVerdict.NoOutcome(),
            SelfUpdateVerdict.ComboBlocked("3.0.1", "'Widget' does not compile against it"),
        ];

        Assert.Equal(
            Enum.GetValues<SelfUpdateOutcome>().OrderBy(o => o),
            all.Select(v => v.Outcome).OrderBy(o => o));
        Assert.All(all, v => Assert.False(string.IsNullOrWhiteSpace(v.Message)));
    }

    /// <summary>
    /// 🚨 "Nothing newer" has to say WHAT IT LOOKED AT. "No newer release" alone is the sentence a
    /// broken checker would also produce; naming the number of tags listed and the installed
    /// version is what makes it evidence rather than a reassurance.
    /// </summary>
    [Fact]
    public void NoNewerRelease_NamesWhatItActuallyLookedAt()
    {
        var verdict = SelfUpdateVerdict.NoNewerRelease(12, "3.0.0-rc8.ci.6183");

        Assert.Contains("12 tag(s) listed", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("3.0.0-rc8.ci.6183", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>A hold with no recorded reason must still read as a hold, never as an empty
    /// sentence — a null reason is exactly the case an operator most needs to see.</summary>
    [Fact]
    public void AHoldWithNoReason_StillNamesTheTagAndTheHold()
    {
        var verdict = SelfUpdateVerdict.Held("3.0.1", null);

        Assert.Equal(SelfUpdateOutcome.Held, verdict.Outcome);
        Assert.Contains("HOLDING 3.0.1", verdict.Message, StringComparison.Ordinal);
        Assert.Equal("3.0.1", verdict.Tag);
    }

    /// <summary>
    /// 🚨 A combo refusal is a DIFFERENT incident from an availability hold, and its sentence has to
    /// say so — the two are fixed in different places (re-verify the candidate vs publish the
    /// missing artifact), and a message that blurred them would send an operator to the wrong one.
    /// </summary>
    [Fact]
    public void ComboBlocked_NamesTheGateAndTheReason()
    {
        var verdict = SelfUpdateVerdict.ComboBlocked(
            "3.0.1", "'Widget' does not compile against it");

        Assert.Equal(SelfUpdateOutcome.ComboBlocked, verdict.Outcome);
        Assert.Contains("combo gate", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("Widget", verdict.Message, StringComparison.Ordinal);
        Assert.Equal("3.0.1", verdict.Tag);
    }

    /// <summary>
    /// 🚨 An unverified roll must leave a DURABLE trace, not only a log line: a log line depends on
    /// a per-category level a deployment may never have set, and that is exactly how an install sat
    /// three builds behind for hours with nothing able to say so. The qualification rides the
    /// verdict, so it lands on LastCheckVerdict — and it never erases what the check did.
    /// </summary>
    [Fact]
    public void Unverified_QualifiesAVerdictWithoutErasingIt()
    {
        var applied = SelfUpdateVerdict.Applied("3.0.1", "3.0.0", null);

        var qualified = applied.Unverified("no combo verification has been recorded for '3.0.1'");

        Assert.Equal(applied.Outcome, qualified.Outcome);
        Assert.Equal(applied.Tag, qualified.Tag);
        Assert.Contains("applied update 3.0.1", qualified.Message, StringComparison.Ordinal);
        Assert.Contains("UNVERIFIED", qualified.Message, StringComparison.Ordinal);
        Assert.Contains("no combo verification has been recorded", qualified.Message,
            StringComparison.Ordinal);
    }

    /// <summary>The structural backstop names itself as a defect in the service, not as a state of
    /// the install — otherwise it would be read as one more thing about the deployment.</summary>
    [Fact]
    public void NoOutcome_BlamesTheService_NotTheInstall()
    {
        var verdict = SelfUpdateVerdict.NoOutcome();

        Assert.Contains("SelfUpdateHostedService", verdict.Message, StringComparison.Ordinal);
        Assert.False(verdict.FoundNewerRelease);
    }
}
