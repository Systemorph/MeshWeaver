using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Activities;
using MeshWeaver.Fixture;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Data.Test;

public class ActivityTest(ITestOutputHelper output) : HubTestBase(output)
{
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
        taskComplete.Status.Should().Be(TaskStatus.RanToCompletion);
        taskComplete2.Status.Should().Be(TaskStatus.RanToCompletion);
    }
}
