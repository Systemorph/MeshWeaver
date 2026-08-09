using Memex.Portal.Shared.SelfUpdate;
using Xunit;

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
            new UpdatePolicyContent { LatestAvailableTag = "3.0.0-ci.2400" }, Running);

        Assert.Equal(PlatformUpdateAvailability.UpdateAvailable, status.Availability);
        Assert.Equal("3.0.0-ci.2400", status.LatestVersion);
    }

    [Fact]
    public void NoRecordedTag_IsUpToDate()
    {
        // The poller records ONLY when it finds something newer, so an empty tag IS the up-to-date signal.
        var status = PlatformUpdateStatus.Derive(new UpdatePolicyContent(), Running);

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
            new UpdatePolicyContent { LatestAvailableTag = Running }, Running);

        Assert.Equal(PlatformUpdateAvailability.UpToDate, status.Availability);
        Assert.Null(status.LatestVersion);
    }

    [Fact]
    public void OlderRecordedTag_IsUpToDate()
    {
        var status = PlatformUpdateStatus.Derive(
            new UpdatePolicyContent { LatestAvailableTag = "3.0.0-ci.2000" }, Running);

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
            new UpdatePolicyContent { LatestAvailableTag = "3.1.0" }, "unknown");

        Assert.Equal(PlatformUpdateAvailability.UpToDate, status.Availability);
    }
}
