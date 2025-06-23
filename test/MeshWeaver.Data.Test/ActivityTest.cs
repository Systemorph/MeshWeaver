using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Activities;
using MeshWeaver.Fixture;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Tests for Activity operations including lifecycle management and sub-activities
/// </summary>
public class ActivityTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Tests basic activity creation, sub-activity management, and completion with status validation
    /// </summary>
    [Fact]
    public async Task TestActivity()
    {
        var activity = new Activity("MyActivity", GetClient());
        var subActivity = activity.StartSubActivity("gugus");

        activity.LogInformation(nameof(activity));
        subActivity.LogInformation(nameof(subActivity));

        var closeTask = activity.Complete(log =>
        {
            log.Should().NotBeNull();
            log.Status.Should().Be(ActivityStatus.Succeeded);
            log.SubActivities.Should().HaveCount(1);
            log.SubActivities.First().Value.Status.Should().Be(ActivityStatus.Succeeded);
        });
        //subActivity.Complete();
        await subActivity.Complete(l =>
        {
            l.Status.Should().Be(ActivityStatus.Succeeded);
        });
        await closeTask;
    }

    /// <summary>
    /// Tests automatic completion behavior of activities when sub-activities are completed
    /// </summary>
    [Fact]
    public async Task TestAutoCompletion()
    {
        var activity = new Activity("MyActivity", GetClient());
        var subActivity = activity.StartSubActivity("gugus");
        var taskComplete = activity.Complete();
        ActivityLog activityLog = null;
        var taskComplete2 = activity.Complete(log => activityLog = log);
        activityLog.Should().BeNull();
        await subActivity.Complete();

        await activity.Complete(log =>
        {
            log.Status.Should().Be(ActivityStatus.Succeeded);
            log.SubActivities.Should().HaveCount(1);
            activityLog.Should().Be(log);
        });
        await DisposeAsync();
        taskComplete.Status.Should().Be(TaskStatus.RanToCompletion);
        taskComplete2.Status.Should().Be(TaskStatus.RanToCompletion);
    }
}
