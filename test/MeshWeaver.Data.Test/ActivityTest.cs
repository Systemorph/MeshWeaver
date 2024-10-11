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
    public void TestActivity()
    {
        var activity = new Activity("MyActivity", GetClient());
        var subActivity = activity.StartSubActivity("gugus");

        activity.LogInformation(nameof(activity));
        subActivity.LogInformation(nameof(subActivity));

        ActivityLog activityLog = null;
        activity.OnCompleted(log => activityLog = log);
        activityLog.Should().BeNull();

        subActivity.Complete();
        ActivityLog subActivityLog = null;
        subActivity.OnCompleted(l => subActivityLog = l);
        subActivityLog.Should().NotBeNull();
        subActivityLog.Status.Should().Be(ActivityStatus.Succeeded);

        activity.Complete();
        activityLog.Should().NotBeNull();
        activityLog.Status.Should().Be(ActivityStatus.Succeeded);
        activityLog.SubActivities.Should().HaveCount(1);
        activityLog.SubActivities.First().Value.Status.Should().Be(ActivityStatus.Succeeded);
    }

    [Fact]
    public async Task TestAutoCompletion()
    {
        var activity = new Activity("MyActivity", GetClient());
        var subActivity = activity.StartSubActivity("gugus");
        activity.CompleteOnSubActivities();
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
