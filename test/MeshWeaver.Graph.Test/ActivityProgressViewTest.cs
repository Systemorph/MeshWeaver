using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the generic activity GUI's control shape — the progress indicator and the
/// message log are built from framework controls (a <see cref="ProgressControl"/>
/// while running, a status label once terminal, and a per-message row list), NOT
/// hand-rolled HTML. Mirrors <see cref="ActivityCancelVisibilityTest"/>: the view's
/// builders are pure functions of the <see cref="ActivityLog"/>, so their control
/// shape is unit-testable without spinning up a layout host.
/// </summary>
public class ActivityProgressViewTest
{
    private static ActivityLog Running(params (string Text, LogLevel Level)[] messages) =>
        new("test")
        {
            Status = ActivityStatus.Running,
            Messages = messages
                .Select(m => new LogMessage(m.Text, m.Level))
                .ToImmutableList(),
        };

    [Fact]
    public void Running_ProgressIndicator_IsIndeterminateProgressBar()
    {
        var log = Running(("Serializing…", LogLevel.Information), ("Committing on HEAD…", LogLevel.Information));

        var indicator = ActivityLayoutAreas.BuildProgressIndicator(log);

        var progress = indicator.Should().BeOfType<ProgressControl>().Subject;
        // Indeterminate: a null Progress value is what drives the animated FluentProgress.
        progress.Progress.Should().BeNull("a running activity has no numeric percentage — the bar is indeterminate");
        // The bar's message is the LATEST log line (the current progress message).
        progress.Message.Should().Be("Committing on HEAD…");
        progress.HideNumber.Should().Be(true);
    }

    [Fact]
    public void Running_NoMessages_ProgressIndicator_ShowsRunning()
    {
        var indicator = ActivityLayoutAreas.BuildProgressIndicator(Running());

        var progress = indicator.Should().BeOfType<ProgressControl>().Subject;
        progress.Progress.Should().BeNull();
        progress.Message.Should().Be("Running…");
    }

    [Fact]
    public void Succeeded_ProgressIndicator_IsDoneStatus_NotAProgressBar()
    {
        var log = new ActivityLog("test")
        {
            Status = ActivityStatus.Succeeded,
            Messages = ImmutableList.Create(new LogMessage("Committed abcd1234.", LogLevel.Information)),
            End = DateTime.UtcNow,
        };

        var indicator = ActivityLayoutAreas.BuildProgressIndicator(log);

        indicator.Should().NotBeOfType<ProgressControl>("a terminal activity shows a status line, not a live bar");
        var label = indicator.Should().BeOfType<LabelControl>().Subject;
        label.Data!.ToString().Should().Contain("Done").And.Contain("Committed abcd1234.");
    }

    [Fact]
    public void Failed_ProgressIndicator_IsFailedStatus()
    {
        var log = new ActivityLog("test")
        {
            Status = ActivityStatus.Failed,
            Messages = ImmutableList.Create(new LogMessage("boom", LogLevel.Error)),
            End = DateTime.UtcNow,
        };

        var indicator = ActivityLayoutAreas.BuildProgressIndicator(log);

        indicator.Should().NotBeOfType<ProgressControl>();
        indicator.Should().BeOfType<LabelControl>()
            .Subject.Data!.ToString().Should().Contain("Failed");
    }

    [Fact]
    public void Log_HasOneRowPerMessage()
    {
        var log = Running(
            ("Serializing…", LogLevel.Information),
            ("A warning", LogLevel.Warning),
            ("An error", LogLevel.Error));

        var logStack = ActivityLayoutAreas.BuildLog(log);

        // One rendered row (NamedArea) per log message.
        logStack.Should().BeOfType<StackControl>();
        logStack.Areas.Should().HaveCount(3, "the log renders one control row per message");
    }

    [Fact]
    public void Log_EmptyRunning_HasSingleRunningRow()
    {
        var logStack = ActivityLayoutAreas.BuildLog(Running());

        logStack.Areas.Should().HaveCount(1, "an empty running log shows a single 'Running…' row");
    }

    [Fact]
    public void Header_RendersUserStatusAndTimestamps()
    {
        var log = Running(("hi", LogLevel.Information));

        var header = ActivityLayoutAreas.BuildHeader(log);

        // user label + status label + timestamp hint.
        header.Areas.Should().HaveCount(3);
    }
}
