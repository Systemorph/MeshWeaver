using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
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
        activity.LogInformation("Starting Sub-activity {Activity}", subActivity.Id);
        
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
    /// Activity with error messages should report Failed status, not Succeeded.
    /// </summary>
    [Fact]
    public async Task Activity_WithError_ReportsFailedStatus()
    {
        var activity = new Activity("MyActivity", GetClient());
        var subActivity = activity.StartSubActivity("SubTask");

        // Log an error on the sub-activity
        subActivity.LogError("Something went wrong");

        // Complete sub-activity
        subActivity.Complete();

        // Wait for main activity to auto-complete
        var log = await activity.Completion
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken);

        log.Should().NotBeNull();
        log.Status.Should().Be(ActivityStatus.Failed,
            "activity with error messages should report Failed, not Succeeded");
    }

    /// <summary>
    /// When LogError and Complete are called in order from the SAME context
    /// (both post to the same hub), the error is processed before completion.
    /// This is the correct pattern — simulates the fix in WorkspaceOperations
    /// where Complete() is called inside the stream.Update lambda after LogError.
    /// </summary>
    [Fact]
    public async Task Activity_ErrorThenCompleteInOrder_ReportsFailedStatus()
    {
        var client = GetClient();
        var activity = new Activity("MyActivity", client, autoClose: false);
        var subActivity = activity.StartSubActivity("DataUpdate");

        // Both LogError and Complete are posted to the same hub — order preserved
        subActivity.LogError("Error updating Data Stream: MeshNode not found");
        subActivity.Complete();

        activity.Complete();

        var log = await activity.Completion
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken);

        log.Should().NotBeNull();
        log.Status.Should().Be(ActivityStatus.Failed,
            "activity should be Failed when sub-activity logged error before completing");
    }

    /// <summary>
    /// Simulates the WorkspaceOperations → DataExtensions flow after fix:
    /// LogError and Complete are both called inside the stream.Update lambda
    /// (same thread, same hub queue order). Parent activity sees the error.
    /// </summary>
    [Fact]
    public async Task Activity_WorkspaceOperationsFlow_ParentSeesSubActivityError()
    {
        var client = GetClient();
        var activity = new Activity("DataUpdate", client, autoClose: false);
        var subActivity = activity.StartSubActivity("StreamUpdate");

        // Simulate stream.Update lambda: LogError then Complete (same thread, same hub)
        subActivity.LogError("Error updating Data Stream: Skipping 1 instances with null key");
        subActivity.Complete();

        // Parent activity completes — simulates DataExtensions line 557-561
        activity.Complete();

        var finalLog = await activity.Completion
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken);

        finalLog.Status.Should().Be(ActivityStatus.Failed,
            "activity should be Failed when sub-activity had errors");
    }
}
