using FluentAssertions;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the contract that the activity Cancel button is visible if-and-only-if
/// the activity is currently running AND no cancel request is already in flight.
/// The three layout-area views (Overview, Progress, CancelButton) all use the
/// shared <see cref="ActivityLayoutAreas.IsCancelButtonVisible"/> predicate;
/// this test fixes the predicate's truth table so a future refactor can't
/// silently surface a Cancel button on a terminal activity (which would write
/// RequestedStatus on a finished log — a no-op at best, a confused-user race
/// at worst).
/// </summary>
public class ActivityCancelVisibilityTest
{
    [Fact]
    public void Running_NoRequest_ShowsButton()
    {
        var log = new ActivityLog("test") { Status = ActivityStatus.Running };
        ActivityLayoutAreas.IsCancelButtonVisible(log).Should().BeTrue(
            "user must be able to cancel a live activity");
    }

    [Fact]
    public void Running_CancelAlreadyRequested_HidesButton()
    {
        var log = new ActivityLog("test")
        {
            Status = ActivityStatus.Running,
            RequestedStatus = ActivityStatus.Cancelled
        };
        ActivityLayoutAreas.IsCancelButtonVisible(log).Should().BeFalse(
            "a cancel request is already in flight; clicking again would be a "
            + "no-op patch on the activity content and risks double-handling");
    }

    [Theory]
    [InlineData(ActivityStatus.Succeeded)]
    [InlineData(ActivityStatus.Failed)]
    [InlineData(ActivityStatus.Cancelled)]
    public void Terminal_HidesButton(ActivityStatus terminalStatus)
    {
        var log = new ActivityLog("test") { Status = terminalStatus };
        ActivityLayoutAreas.IsCancelButtonVisible(log).Should().BeFalse(
            "activity is in terminal state {0} — cancel is meaningless",
            terminalStatus);
    }

    [Theory]
    [InlineData(ActivityStatus.Succeeded)]
    [InlineData(ActivityStatus.Failed)]
    [InlineData(ActivityStatus.Cancelled)]
    public void Terminal_WithStaleCancelRequest_StillHides(ActivityStatus terminalStatus)
    {
        // Defensive: even if RequestedStatus was set during the run, once the
        // activity reached a terminal state the Cancel button is gone. Without
        // this assertion a regression could re-introduce the "cancel a settled
        // activity" footgun.
        var log = new ActivityLog("test")
        {
            Status = terminalStatus,
            RequestedStatus = ActivityStatus.Cancelled
        };
        ActivityLayoutAreas.IsCancelButtonVisible(log).Should().BeFalse(
            "terminal status wins over RequestedStatus for visibility");
    }
}
