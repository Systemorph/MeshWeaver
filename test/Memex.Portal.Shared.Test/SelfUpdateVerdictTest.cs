using System;
using System.Linq;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Hosting.SelfUpdate;
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
    // A migration refusal is a release that WAS waiting and could not be taken — the report must
    // fire for it exactly as for a hold; a schema that did not move is not "nothing newer".
    [InlineData(SelfUpdateOutcome.MigrationFailed, true)]
    [InlineData(SelfUpdateOutcome.NoNewerRelease, false)]
    [InlineData(SelfUpdateOutcome.UpdatesDisabled, false)]
    [InlineData(SelfUpdateOutcome.CheckFailed, false)]
    [InlineData(SelfUpdateOutcome.NoOutcome, false)]
    // A restart activates a MODULE this install already landed — nothing newer was waiting in
    // the registry, so a safety-net check that restarts must never fire the dead-channel report.
    [InlineData(SelfUpdateOutcome.Restarted, false)]
    [InlineData(SelfUpdateOutcome.RestartDeferred, false)]
    [InlineData(SelfUpdateOutcome.RestartUnavailable, false)]
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
            SelfUpdateVerdict.MigrationFailed("3.0.1", MigrationRunOutcome.TimedOut),
            SelfUpdateVerdict.InstalledTagWithdrawn("3.1.0-ci.7841", "it is not in the registry"),
            SelfUpdateVerdict.Restarted(SelfUpdateVerdict.NoNewerRelease(7, "3.0.0"), "3.0.0", null),
            SelfUpdateVerdict.RestartDeferred(
                SelfUpdateVerdict.NoNewerRelease(7, "3.0.0"), "3.0.0", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)),
            SelfUpdateVerdict.RestartUnavailable(
                SelfUpdateVerdict.NoNewerRelease(7, "3.0.0"), "3.0.0", "this install does not self-patch"),
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

    /// <summary>
    /// 🚨 A strand is not a "nothing newer" (#3543). The verdict has to name the version that stopped
    /// resolving, say what is wrong in a word an operator can search for, and name the move — because
    /// nothing in the process can make this one better on its own: no publication is ever "newer"
    /// than a tag that already outranks everything left in the registry.
    /// </summary>
    [Fact]
    public void InstalledTagWithdrawn_NamesTheVersion_AndTheOperatorsMove()
    {
        var verdict = SelfUpdateVerdict.InstalledTagWithdrawn(
            "3.1.0-ci.7841", "the installed 3.1.0-ci.7841 is NOT among the 500 platform tag(s)");

        Assert.Equal(SelfUpdateOutcome.InstalledTagWithdrawn, verdict.Outcome);
        Assert.Contains("STRANDED on 3.1.0-ci.7841", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("kubectl set image", verdict.Message, StringComparison.Ordinal);
        Assert.Equal("3.1.0-ci.7841", verdict.UnresolvedInstalledTag);
        Assert.False(verdict.FoundNewerRelease,
            "a strand is the opposite of 'a release was waiting' — reporting it as one would fire the "
            + "dead-event-channel warning at an install whose event channel is fine");
    }

    /// <summary>
    /// 🚨 A recovery roll goes BACKWARDS in lineage, on purpose, and must say so on the verdict — not
    /// only in a log line, whose level a deployment may never have set. It qualifies what the check
    /// did without erasing it, exactly as <c>Unverified</c> does, and carries the version that stopped
    /// resolving so the Updates tab can render the state too.
    /// </summary>
    [Fact]
    public void Recovering_QualifiesTheRoll_AndCarriesTheWithdrawnVersion()
    {
        var applied = SelfUpdateVerdict.Applied("3.0.0-ci.7977", "3.1.0-ci.7841", null);

        var qualified = applied.Recovering(
            "3.1.0-ci.7841", "the installed 3.1.0-ci.7841 is NOT among the 500 platform tag(s)");

        Assert.Equal(SelfUpdateOutcome.Applied, qualified.Outcome);
        Assert.Equal("3.0.0-ci.7977", qualified.Tag);
        Assert.Contains("applied update 3.0.0-ci.7977", qualified.Message, StringComparison.Ordinal);
        Assert.Contains("RECOVERY", qualified.Message, StringComparison.Ordinal);
        Assert.Equal("3.1.0-ci.7841", qualified.UnresolvedInstalledTag);
    }

    /// <summary>
    /// 🚨 The third state has to be SAID. "Nothing newer" over a listing that could not answer whether
    /// the installed tag still exists is a claim with no evidence behind half of it — the same defect
    /// #2553 removed one level up, and the reason that sentence is qualified rather than reused.
    /// </summary>
    [Fact]
    public void InstalledTagUnchecked_SaysTheQuestionWasNotAnswered()
    {
        var verdict = SelfUpdateVerdict.NoNewerRelease(0, "3.0.0-ci.7977")
            .InstalledTagUnchecked("the registry listing carried no platform version tags at all");

        Assert.Equal(SelfUpdateOutcome.NoNewerRelease, verdict.Outcome);
        Assert.Contains("NOT established", verdict.Message, StringComparison.Ordinal);
        Assert.Null(verdict.UnresolvedInstalledTag);
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

    // ══════════════════════════════════════════════════════════════════════════
    //  A pending restart rolls the same image, within the interval rules (#3650)
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly SelfUpdateVerdict UpToDate = SelfUpdateVerdict.NoNewerRelease(7, "3.0.0");

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 🚨 Only a check that PATCHED nothing has a restart to take. An applied roll IS the restart;
    /// a refused migration leaves the image where it is on purpose; a failed check decided
    /// nothing; a disabled policy means never; and the structural backstop is not a verdict at
    /// all. Every other outcome — up to date, held, deferred, detect-only, combo-blocked, a strand
    /// — may be followed by the restart of the image that IS running.
    /// </summary>
    [Theory]
    [InlineData(SelfUpdateOutcome.NoNewerRelease, true)]
    [InlineData(SelfUpdateOutcome.Held, true)]
    [InlineData(SelfUpdateOutcome.Deferred, true)]
    [InlineData(SelfUpdateOutcome.DetectOnly, true)]
    [InlineData(SelfUpdateOutcome.ComboBlocked, true)]
    [InlineData(SelfUpdateOutcome.InstalledTagWithdrawn, true)]
    [InlineData(SelfUpdateOutcome.Applied, false)]
    [InlineData(SelfUpdateOutcome.MigrationFailed, false)]
    [InlineData(SelfUpdateOutcome.CheckFailed, false)]
    [InlineData(SelfUpdateOutcome.UpdatesDisabled, false)]
    [InlineData(SelfUpdateOutcome.NoOutcome, false)]
    public void MayRestartAfter_OnlyWhenTheCheckPatchedNothing(SelfUpdateOutcome outcome, bool expected)
        => Assert.Equal(expected, SelfUpdateVerdict.MayRestartAfter(new SelfUpdateVerdict(outcome, "…")));

    /// <summary>Inside the floor the restart is deferred, naming how long ago the install rolled
    /// and the floor it sits inside — the same sentence shape a deferred roll uses.</summary>
    [Fact]
    public void RestartDeferredBy_InsideTheFloor_Defers()
    {
        var verdict = SelfUpdateVerdict.RestartDeferredBy(
            UpToDate, "3.0.0", Now - TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), Now);

        Assert.NotNull(verdict);
        Assert.Equal(SelfUpdateOutcome.RestartDeferred, verdict!.Outcome);
        Assert.Contains("00:05:00", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("01:00:00", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("deferring the restart", verdict.Message, StringComparison.Ordinal);
        Assert.Equal("3.0.0", verdict.Tag);
    }

    /// <summary>Past the floor, at the floor, never rolled, floor disabled: the restart proceeds.</summary>
    [Fact]
    public void RestartDeferredBy_OutsideTheFloor_NeverRolled_OrFloorOff_Proceeds()
    {
        var floor = TimeSpan.FromHours(1);

        Assert.Null(SelfUpdateVerdict.RestartDeferredBy(UpToDate, "3.0.0", Now - TimeSpan.FromHours(2), floor, Now));
        Assert.Null(SelfUpdateVerdict.RestartDeferredBy(UpToDate, "3.0.0", Now - floor, floor, Now));
        Assert.Null(SelfUpdateVerdict.RestartDeferredBy(UpToDate, "3.0.0", lastRolledAt: null, floor, Now));
        Assert.Null(SelfUpdateVerdict.RestartDeferredBy(UpToDate, "3.0.0", Now - TimeSpan.FromSeconds(1), TimeSpan.Zero, Now));
    }

    /// <summary>
    /// A restart verdict KEEPS the platform verdict it followed: the record still has to say what
    /// the check found about the registry, and the restart is the second sentence, not a
    /// replacement for the first.
    /// </summary>
    [Fact]
    public void Restarted_CarriesThePlatformVerdict_AndNamesTheImage()
    {
        var verdict = SelfUpdateVerdict.Restarted(UpToDate, "3.0.0", Now);

        Assert.Equal(SelfUpdateOutcome.Restarted, verdict.Outcome);
        Assert.StartsWith(UpToDate.Message, verdict.Message, StringComparison.Ordinal);
        Assert.Contains("RESTARTED on 3.0.0", verdict.Message, StringComparison.Ordinal);
        Assert.Equal("3.0.0", verdict.Tag);
        Assert.False(verdict.FoundNewerRelease);
    }

    /// <summary>
    /// 🚨 A landed module nothing will ever activate is a state an operator has to see and can
    /// act on: the verdict names why this install cannot restart itself and the move.
    /// </summary>
    [Fact]
    public void RestartUnavailable_NamesTheReason_AndTheOperatorsMove()
    {
        var verdict = SelfUpdateVerdict.RestartUnavailable(
            UpToDate, "3.0.0", "this install does not self-patch (detect-and-notify)");

        Assert.Equal(SelfUpdateOutcome.RestartUnavailable, verdict.Outcome);
        Assert.Contains("does not self-patch", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("kubectl rollout restart", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>A restart taken on the recovery path keeps the strand visible: the withdrawn
    /// tag the platform verdict carried survives onto the restart verdict.</summary>
    [Fact]
    public void ARestartVerdict_KeepsTheUnresolvedInstalledTag()
    {
        var stranded = SelfUpdateVerdict.NoNewerRelease(0, "3.1.0-ci.7841")
            .Recovering("3.1.0-ci.7841", "the installed tag is NOT among the listed tags");

        Assert.Equal("3.1.0-ci.7841",
            SelfUpdateVerdict.Restarted(stranded, "3.1.0-ci.7841", null).UnresolvedInstalledTag);
        Assert.Equal("3.1.0-ci.7841",
            SelfUpdateVerdict.RestartUnavailable(stranded, "3.1.0-ci.7841", "detect-only").UnresolvedInstalledTag);
    }
}
