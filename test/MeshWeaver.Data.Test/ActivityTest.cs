using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Microsoft.Extensions.Logging;
using Xunit;

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
        var client = GetClient();
        var activity = new Activity("MyActivity", client);
        
        // Start sub-activity using message-based approach
        var subActivity = activity.StartSubActivity("gugus");

        // Log information using message-based approach
        PostLogRequest(activity, LogLevel.Information, nameof(activity));
        
        var closeTask = activity.Completion;
        
        // Complete activity using message-based approach

        subActivity.Complete();
        
        
        var log = await closeTask
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken)
            ;
        log.Should().NotBeNull();
        log.Status.Should().Be(ActivityStatus.Succeeded);
    }

    /// <summary>
    /// Tests automatic completion behavior of activities when sub-activities are completed
    /// </summary>
    [Fact]
    public async Task TestAutoCompletion()
    {
        var activity = new Activity("MyActivity", GetClient());
        
        // Start sub-activity using message-based approach
        var subActivity = activity.StartSubActivity("gugus");
        
        var taskComplete = activity.Completion;
        ActivityLog? activityLog = null;
        var taskComplete2 = activity.Completion; // Both should refer to the same completion
        
        // Initially activityLog should be null
        activityLog.Should().BeNull();
        
        // Complete activity using message-based approach
        subActivity.Complete();
        
        // Wait for the main activity to complete before disposal
        activityLog = await taskComplete
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken)
            ;
        
        await DisposeAsync();
        taskComplete.Status.Should().Be(TaskStatus.RanToCompletion);
        taskComplete2.Status.Should().Be(TaskStatus.RanToCompletion);
        activityLog.Should().NotBeNull();
        activityLog.Status.Should().Be(ActivityStatus.Succeeded);
    }

    /// <summary>
    /// Helper method to post log requests to activity
    /// </summary>
    private void PostLogRequest(Activity activity, LogLevel logLevel, string message, params object[] args)
    {
        var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
        var logMessage = new LogMessage(formattedMessage, logLevel);
        GetClient().Post(new LogRequest(logMessage), o => o.WithTarget(activity.Address));
    }
}
