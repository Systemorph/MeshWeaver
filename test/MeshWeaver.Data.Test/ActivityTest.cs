using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Activities;
using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Data.Test;

public class ActivityTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Inject] private ILogger<ActivityTest> logger;

    [Fact]
    public async Task TestActivity()
    {
        var activity = new Activity("MyActivity", GetClient());
        var subActivity = activity.StartSubActivity("gugus");

        activity.LogInformation(nameof(activity));
        subActivity.LogInformation(nameof(subActivity));

        var activityTcs = new TaskCompletionSource(new CancellationTokenSource(3.Seconds()));
        activity.OnCompleted(log =>
        {
            log.Should().NotBeNull();
            log.Status.Should().Be(ActivityStatus.Succeeded);
            log.SubActivities.Should().HaveCount(1);
            log.SubActivities.First().Value.Status.Should().Be(ActivityStatus.Succeeded);
            activityTcs.SetResult();
        });
        subActivity.Complete();
        var subActivityTcs = new TaskCompletionSource(new CancellationTokenSource(3.Seconds()));
        subActivity.OnCompleted(l =>
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
        activity.OnCompleted(log => activityLog = log);
        activityLog.Should().BeNull();
        subActivity.Complete();

        var tcs = new TaskCompletionSource(new CancellationTokenSource(1.Seconds()));
        activity.OnCompleted(log =>
        {
            log.Status.Should().Be(ActivityStatus.Succeeded);
            log.SubActivities.Should().HaveCount(1);
            activityLog.Should().Be(log);
            tcs.SetResult();
        });
        await tcs.Task;
    }
}
