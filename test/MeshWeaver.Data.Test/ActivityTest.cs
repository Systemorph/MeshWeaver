using FluentAssertions;
using FluentAssertions.Extensions;
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

        var activityTcs = new TaskCompletionSource(new CancellationTokenSource(1.Seconds()).Token);
        activity.Complete(log =>
        {
            log.Should().NotBeNull();
            log.Status.Should().Be(ActivityStatus.Succeeded);
            log.SubActivities.Should().HaveCount(1);
            log.SubActivities.First().Value.Status.Should().Be(ActivityStatus.Succeeded);
            activityTcs.SetResult();
        });
        //subActivity.Complete();
        var subActivityTcs = new TaskCompletionSource(new CancellationTokenSource(1.Seconds()).Token);
        subActivity.Complete(l =>
        {
            l.Status.Should().Be(ActivityStatus.Succeeded);
            subActivityTcs.SetResult();
        });
        activity.Complete();
        await subActivityTcs.Task;
        await activityTcs.Task;
    }

    [Fact]
    public async Task TestAutoCompletion()
    {
        var activity = new Activity("MyActivity", GetClient());
        var subActivity = activity.StartSubActivity("gugus");
        activity.Complete();
        ActivityLog activityLog = null;
        activity.Complete(log => activityLog = log);
        activityLog.Should().BeNull();
        subActivity.Complete();

        var tcs = new TaskCompletionSource(new CancellationTokenSource(1.Seconds()).Token);
        activity.Complete(log =>
        {
            log.Status.Should().Be(ActivityStatus.Succeeded);
            log.SubActivities.Should().HaveCount(1);
            activityLog.Should().Be(log);
            tcs.SetResult();
        });
        await tcs.Task;
    }
}
