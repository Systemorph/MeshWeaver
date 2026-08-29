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
