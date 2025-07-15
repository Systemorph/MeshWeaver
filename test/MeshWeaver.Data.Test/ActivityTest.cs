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
        
        // Start sub-activity using message-based approach
        GetClient().Post(new StartSubActivityRequest("gugus"), 
            options => options.WithTarget(activity.ActivityAddress));

        // Log information using message-based approach
        PostLogRequest(activity, LogLevel.Information, nameof(activity));
        
        var closeTask = activity.Completion;
        
        // Complete activity using message-based approach

        GetClient().Post(new CompleteActivityRequest(null), 
            options => options.WithTarget(activity.ActivityAddress));
        
        
        var log = await closeTask;
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
        GetClient().Post(new StartSubActivityRequest("gugus"), 
            options => options.WithTarget(activity.ActivityAddress));
        
        var taskComplete = activity.Completion;
        ActivityLog? activityLog = null;
        var taskComplete2 = activity.Completion; // Both should refer to the same completion
        
        // Initially activityLog should be null
        activityLog.Should().BeNull();
        
        // Complete activity using message-based approach
        GetClient().Post(new CompleteActivityRequest(null), 
            options => options.WithTarget(activity.ActivityAddress));
        
        // Wait for the main activity to complete before disposal
        activityLog = await taskComplete;
        
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
        GetClient().Post(new LogRequest(activity.ActivityAddress, logMessage));
    }
}
