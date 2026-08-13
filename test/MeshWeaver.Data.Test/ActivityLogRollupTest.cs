using System.Collections.Immutable;
using MeshWeaver.Data;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the <see cref="ActivityLog"/> roll-ups — <see cref="ActivityLog.MessageCount"/> and
/// <see cref="ActivityLog.MaxSeverity"/> — that make an activity's terminal status and error
/// predicate a function of COUNTERS rather than a scan of <see cref="ActivityLog.Messages"/>.
///
/// <para>Why this matters: <c>Messages</c> is on its way to becoming a bounded window (older lines
/// flushed to satellites), so anything derived from "the whole list" silently changes meaning the
/// moment an activity outgrows it. <c>Finish()</c> was exactly that — its status came from
/// <c>Messages.Max(LogLevel)</c>, so a long failing activity whose error line had scrolled out of the
/// window would have finished <c>Succeeded</c>.</para>
///
/// <para>The second half of the file is the BACK-COMPATIBILITY half: a log written before the
/// roll-ups existed carries <c>MessageCount = 0</c> and <c>MaxSeverity = Trace</c>, and must still
/// produce byte-identical answers. Pure-function tests — deterministic, no hub.</para>
/// </summary>
public class ActivityLogRollupTest
{
    private static LogMessage Msg(LogLevel level) => new($"{level}", level);

    [Fact]
    public void Append_AdvancesCountAndSeverity()
    {
        var log = new ActivityLog("Test")
            .Append(Msg(LogLevel.Information))
            .Append([Msg(LogLevel.Debug), Msg(LogLevel.Warning)]);

        Assert.Equal(3, log.MessageCount);
        Assert.Equal(3, log.TotalMessageCount);
        Assert.Equal(LogLevel.Warning, log.MaxSeverity);
        Assert.Equal(3, log.Messages.Count);
    }

    [Fact]
    public void Append_Empty_IsANoOp()
    {
        var log = new ActivityLog("Test").Append(Msg(LogLevel.Information));
        Assert.Same(log, log.Append(ImmutableList<LogMessage>.Empty));
    }

    /// <summary>
    /// <see cref="LogLevel.None"/> is numerically ABOVE Critical but means "no logging" — rolling it
    /// up would turn a silenced line into the worst thing that ever happened to the activity.
    /// </summary>
    [Fact]
    public void Append_NeverRollsUpNone()
    {
        var log = new ActivityLog("Test").Append([Msg(LogLevel.Warning), Msg(LogLevel.None)]);

        Assert.Equal(LogLevel.Warning, log.MaxSeverity);
        Assert.Equal(ActivityStatus.Warning, log.Finish(1, null).Status);
    }

    /// <summary>
    /// 🚨 THE REGRESSION GUARD for the window migration. The severity that decides the terminal status
    /// must survive the messages leaving <c>Messages</c>. Dropping the list here stands in for the
    /// window having scrolled past the error line.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Information, ActivityStatus.Succeeded)]
    [InlineData(LogLevel.Warning, ActivityStatus.Warning)]
    [InlineData(LogLevel.Error, ActivityStatus.Failed)]
    [InlineData(LogLevel.Critical, ActivityStatus.Failed)]
    public void FinalStatus_SurvivesTheMessagesLeavingTheWindow(LogLevel level, ActivityStatus expected)
    {
        var full = new ActivityLog("Test").Append(Msg(level));
        var windowed = full with { Messages = ImmutableList<LogMessage>.Empty };

        Assert.Equal(expected, full.Finish(1, null).Status);
        Assert.Equal(expected, windowed.Finish(1, null).Status);
        Assert.Equal(level >= LogLevel.Error, windowed.HasErrors());
    }

    /// <summary>A sub-activity's status still rolls up, and still wins when it is the more severe one.</summary>
    [Fact]
    public void FinalStatus_TakesTheWorseOfSubActivitiesAndSeverity()
    {
        var failedChild = new ActivityLog("Child") { Status = ActivityStatus.Failed };
        var parent = new ActivityLog("Parent")
        {
            SubActivities = [failedChild]
        }.Append(Msg(LogLevel.Information));

        Assert.Equal(ActivityStatus.Failed, parent.Finish(1, null).Status);
    }

    /// <summary><c>Fail()</c> routes through <c>Append</c>, so it advances both roll-ups.</summary>
    [Fact]
    public void Fail_AdvancesTheRollups()
    {
        var log = new ActivityLog("Test").Fail("boom");

        Assert.Equal(1, log.MessageCount);
        Assert.Equal(LogLevel.Error, log.MaxSeverity);
        Assert.Equal(ActivityStatus.Failed, log.Status);
        Assert.True(log.HasErrors());
    }

    // ---- back-compatibility with logs persisted before the roll-ups existed ----

    /// <summary>
    /// A pre-roll-up log has <c>MessageCount = 0</c> while <c>Messages</c> is non-empty. Every derived
    /// answer must come out exactly as the old list-scanning code produced it.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Trace, ActivityStatus.Succeeded, false)]
    [InlineData(LogLevel.Information, ActivityStatus.Succeeded, false)]
    [InlineData(LogLevel.Warning, ActivityStatus.Warning, false)]
    [InlineData(LogLevel.Error, ActivityStatus.Failed, true)]
    public void LegacyLog_WithoutRollups_AnswersFromTheListExactlyAsBefore(
        LogLevel level, ActivityStatus expectedStatus, bool expectedHasErrors)
    {
        // Exactly the shape JSON written before MessageCount/MaxSeverity existed deserializes into.
        var legacy = new ActivityLog("Test") { Messages = [Msg(level)] };

        Assert.Equal(0, legacy.MessageCount);
        Assert.Equal(LogLevel.Trace, legacy.MaxSeverity);
        Assert.Equal(1, legacy.TotalMessageCount);
        Assert.Equal(expectedStatus, legacy.Finish(1, null).Status);
        Assert.Equal(expectedHasErrors, legacy.HasErrors());
    }

    /// <summary>An activity that logged nothing at all is Succeeded — the old <c>DefaultIfEmpty(Information)</c>.</summary>
    [Fact]
    public void EmptyLog_IsSucceeded()
    {
        var empty = new ActivityLog("Test");

        Assert.Equal(0, empty.TotalMessageCount);
        Assert.Equal(ActivityStatus.Succeeded, empty.Finish(1, null).Status);
        Assert.False(empty.HasErrors());
    }

    /// <summary>An explicit override still wins when it is the more severe outcome.</summary>
    [Fact]
    public void Finish_OverrideWinsWhenMoreSevere()
    {
        var log = new ActivityLog("Test").Append(Msg(LogLevel.Information));

        Assert.Equal(ActivityStatus.Cancelled, log.Finish(1, ActivityStatus.Cancelled).Status);
        // …and never DOWNgrades a failure the log already recorded.
        Assert.Equal(ActivityStatus.Failed,
            log.Append(Msg(LogLevel.Error)).Finish(1, ActivityStatus.Succeeded).Status);
    }
}
