using System;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.PluginCatalog;
using Xunit;
using MeshWeaver.Hosting.SelfUpdate;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the verdict an ORDINARY user is shown on the About tab: "up to date" vs "update available".
/// Two properties matter here and neither is obvious:
/// <list type="bullet">
/// <item>The projection carries the verdict and — only when there IS an update — the newer tag.
/// Everything else on <c>Admin/UpdatePolicy</c> (the strategy, the CI-green flag, the poller's
/// checked-at) is platform-admin data and must not leak through this record.</item>
/// <item>With automatic checks OFF, the answer is <c>Unknown</c>, never "up to date". Nothing is
/// polling, so currency is a claim we have no evidence for — and the About tab renders nothing
/// rather than reassure a user falsely.</item>
/// </list>
/// </summary>
public class PlatformUpdateStatusTest
{
    private const string Running = "3.0.0-ci.2340";

    [Fact]
    public void NewerRecordedTag_IsAnAvailableUpdate()
    {
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, LatestAvailableTag = "3.0.0-ci.2400" }, Running);

        Assert.Equal(PlatformUpdateAvailability.UpdateAvailable, status.Availability);
        Assert.Equal("3.0.0-ci.2400", status.LatestVersion);
    }

    [Fact]
    public void NoRecordedTag_IsUpToDate()
    {
        // The poller records ONLY when it finds something newer, so an empty tag IS the up-to-date signal.
        var status = PlatformUpdateStatus.Derive(new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous }, Running);

        Assert.Equal(PlatformUpdateAvailability.UpToDate, status.Availability);
        Assert.Null(status.LatestVersion);
    }

    [Fact]
    public void AlreadyRolledToTheRecordedTag_IsUpToDate()
    {
        // After the install takes the update, LatestAvailableTag stays behind on the node — equal to
        // (not newer than) the running build. Comparing, rather than testing for presence, is what
        // keeps the tab from claiming an update forever after it was applied.
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, LatestAvailableTag = Running }, Running);

        Assert.Equal(PlatformUpdateAvailability.UpToDate, status.Availability);
        Assert.Null(status.LatestVersion);
    }

    [Fact]
    public void OlderRecordedTag_IsUpToDate()
    {
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, LatestAvailableTag = "3.0.0-ci.2000" }, Running);

        Assert.Equal(PlatformUpdateAvailability.UpToDate, status.Availability);
    }

    [Fact]
    public void ChecksSwitchedOff_WithNothingRecorded_IsUnknown_NotUpToDate()
    {
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent { Policy = UpdatePolicyKind.None }, Running);

        Assert.Equal(PlatformUpdateAvailability.Unknown, status.Availability);
        Assert.Null(status.LatestVersion);
    }

    [Fact]
    public void ChecksSwitchedOff_ButANewerBuildWasRecorded_StillReportsIt()
    {
        // An available update is a fact about the registry, not about whether this install intends to
        // take it — a "None" policy hides the update from nobody.
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent { Policy = UpdatePolicyKind.None, LatestAvailableTag = "3.1.0" },
            Running);

        Assert.Equal(PlatformUpdateAvailability.UpdateAvailable, status.Availability);
        Assert.Equal("3.1.0", status.LatestVersion);
    }

    [Fact]
    public void UnparseableRunningVersion_IsUpToDate_NotAFalseUpdatePrompt()
    {
        // A git-less source drop stamps "unknown"; VersionSelect.IsNewer refuses to compare against it
        // (the same gate that stops the poller auto-rolling). The user must not be nagged on that basis.
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent { Policy = UpdatePolicyKind.Continuous, LatestAvailableTag = "3.1.0" },
            "unknown");

        Assert.Equal(PlatformUpdateAvailability.UpToDate, status.Availability);
    }

    [Fact]
    public void ANewerTagTheAvailabilityGateHolds_ReadsAsHeld_NotAsAvailable()
    {
        // 🚨 An install that has REFUSED a build must not look like one that is about to take it.
        // Rendering a hold as "update available" is how a deployment stays frozen for weeks while
        // every surface says it is fine — the outage the gate must never become.
        //
        // 🚨 Policy is now STATED rather than defaulted (#3812). It always mattered and was never
        // written down: a hold reads as a hold only while a poller is running to clear it, and
        // Policy's default is None. Leaving it implicit made this case indistinguishable from the
        // frozen-record case that FrozenHoldIsHistoryTest pins.
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent
            {
                Policy = UpdatePolicyKind.Continuous,
                LatestAvailableTag = "3.0.0-ci.2400",
                HeldTag = "3.0.0-ci.2400",
                HeldReason = "Store: no sealed content bake for framework identity sabc",
            },
            Running);

        Assert.Equal(PlatformUpdateAvailability.UpdateHeld, status.Availability);
        Assert.Equal("3.0.0-ci.2400", status.LatestVersion);
    }

    [Fact]
    public void AHoldRecordedForADIFFERENTTag_DoesNotHoldTheCurrentCandidate()
    {
        // A stale hold from an earlier candidate must not suppress a later one. The poller clears
        // the hold on every successful tick, but the projection must not depend on it having done so.
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent
            {
                LatestAvailableTag = "3.0.0-ci.2400",
                HeldTag = "3.0.0-ci.2350",
                HeldReason = "an older candidate blocked once",
            },
            Running);

        Assert.Equal(PlatformUpdateAvailability.UpdateAvailable, status.Availability);
    }

    /// <summary>
    /// 🚨 A combo RED (#2274) is read off the recorded verdict itself — not only off the poller's
    /// notes — and under policy <c>platform-rolls-independently-of-packages</c> it renders as what
    /// it is: an ADVISORY about packages, never a
    /// hold. The
    /// build stays "available" (the poller rolls to it) and the failing modules are named on the
    /// status, so it never reads as an unqualified "update available" either.
    /// </summary>
    [Fact]
    public void ACandidateTheComboGateRecordedRed_RendersAsAvailable_WithTheModulesAsAdvisories()
    {
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent
            {
                LatestAvailableTag = "3.0.0-ci.2400",
                ComboVerifications = [Verdict("3.0.0-ci.2400", ComboVerdictKind.Red)],
            },
            Running);

        Assert.Equal(PlatformUpdateAvailability.UpdateAvailable, status.Availability);
        Assert.Equal("3.0.0-ci.2400", status.LatestVersion);
        Assert.Contains("RED", status.Advisories);
    }

    /// <summary>
    /// 🚨 THE ABOUT TAB UNDER POLICY <c>platform-rolls-independently-of-packages</c>. memex reached
    /// partnerre.meshweaver.cloud's
    /// exact state: the poller found 3.1.10502, the availability check could not resolve its
    /// framework identity, and — before the change — the record said HELD and the About tab read
    /// "Update held — a package this deployment runs is not available for that build". Now the
    /// poller records no hold, only the advisory, and the About line says the platform rolls and
    /// points to the Updates tab, where the advisories are listed.
    /// </summary>
    [Fact]
    public void AWaivedPackageHold_RendersAsAvailable_AndTheAboutLineAnnouncesThePackageAdvisories()
    {
        const string Tag = "3.1.10502";
        const string Advisory =
            "the platform rolls independently of packages — package availability for this release could NOT "
            + "be determined, and that decides nothing about the platform roll: the framework identity of "
            + "release 3.1.10502 is not resolvable";
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent
            {
                Policy = UpdatePolicyKind.Continuous,
                LatestAvailableTag = Tag,
                AdvisoriesTag = Tag,
                Advisories = Advisory,
            },
            "3.1.10490");

        Assert.Equal(PlatformUpdateAvailability.UpdateAvailable, status.Availability);
        Assert.Equal(Advisory, status.Advisories);

        var markdown = Memex.Portal.Shared.Settings.AboutSettingsTab.UpdateStatusMarkdown(status, key => $"<{key}>");
        Assert.NotNull(markdown);
        Assert.DoesNotContain("<about.updateHeld>", markdown);
        Assert.Contains("<about.updateAvailable>", markdown);
        Assert.Contains("<about.platformRolls>", markdown);
        // The diagnostics themselves are untranslated server prose: admin-scoped, on the Updates
        // tab — the About line carries the translated sentence only.
        Assert.DoesNotContain("framework identity of release 3.1.10502 is not resolvable", markdown);
    }

    /// <summary>
    /// After the roll, the running build is the one the advisories describe: the About tab reads
    /// "up to date" and still says that package advisories are recorded for it.
    /// </summary>
    [Fact]
    public void AfterTheRoll_UpToDate_StillAnnouncesThePackageAdvisories()
    {
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent
            {
                Policy = UpdatePolicyKind.Continuous,
                AdvisoriesTag = "3.1.10502",
                Advisories = "Feedback: no sealed content bake",
            },
            "3.1.10502+abc123");

        Assert.Equal(PlatformUpdateAvailability.UpToDate, status.Availability);
        Assert.Equal("Feedback: no sealed content bake", status.Advisories);
        Assert.Contains("<about.platformRolls>",
            Memex.Portal.Shared.Settings.AboutSettingsTab.UpdateStatusMarkdown(status, key => $"<{key}>"));
    }

    /// <summary>
    /// 🚨 Only a RED reads as held. "We could not find out" is not a refusal — nothing blocked that
    /// build — and rendering it as one would send an operator to fix an incompatibility that was
    /// never diagnosed. The Updates settings tab renders that state on its own terms.
    /// </summary>
    [Theory]
    [InlineData(ComboVerdictKind.Green)]
    [InlineData(ComboVerdictKind.NotVerifiable)]
    public void ANonRedVerdict_IsNotAHold(ComboVerdictKind kind)
    {
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent
            {
                LatestAvailableTag = "3.0.0-ci.2400",
                ComboVerifications = [Verdict("3.0.0-ci.2400", kind)],
            },
            Running);

        Assert.Equal(PlatformUpdateAvailability.UpdateAvailable, status.Availability);
    }

    /// <summary>A verdict about an EARLIER candidate must not suppress the current one.</summary>
    [Fact]
    public void ARedVerdictForADifferentTag_DoesNotHoldTheCurrentCandidate()
    {
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent
            {
                LatestAvailableTag = "3.0.0-ci.2400",
                ComboVerifications = [Verdict("3.0.0-ci.2350", ComboVerdictKind.Red)],
            },
            Running);

        Assert.Equal(PlatformUpdateAvailability.UpdateAvailable, status.Availability);
    }

    private static ComboVerification Verdict(string tag, ComboVerdictKind kind) => new()
    {
        CandidateTag = tag,
        Verdict = kind,
        VerifiedAt = DateTimeOffset.UtcNow,
    };
}
