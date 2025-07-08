#nullable enable
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities;

public record ProcessActivityCompletionRequest(string ActivityId, ActivityStatus? Status) : IRequest;

public record CompleteActivitySelfRequest(string ActivityId, ActivityStatus? Status) : IRequest;

public record Activity(string Category, IMessageHub Hub) : ActivityBase<Activity>(Category, Hub),
    IMessageHandler<ProcessActivityCompletionRequest>, 
    IMessageHandler<CompleteActivitySelfRequest>
{
    protected void CompleteMyself(ActivityStatus? status)
    {
        var activityId = Id ?? Guid.NewGuid().ToString("N")[..8];
        Logger.LogInformation("Activity {ActivityId} CompleteMyself called with status {Status}, CurrentStatus: {CurrentStatus}, Thread: {ThreadId}", 
            activityId, status, Log.Status, Thread.CurrentThread.ManagedThreadId);

        Update(a =>
        {
            if (a.Log.Status == ActivityStatus.Running)
            {
                Logger.LogDebug("Activity {ActivityId} updating status from Running to completion, has errors: {HasErrors}", 
                    activityId, a.HasErrors());
                
                var finalStatus = status ?? (a.HasErrors() ? ActivityStatus.Failed : ActivityStatus.Succeeded);
                var ret = a.WithLog(log => log with
                {
                    Status = finalStatus,
                    End = DateTime.UtcNow,
                    Version = log.Version + 1
                });
                
                Logger.LogDebug("Activity {ActivityId} executing {ActionCount} completion actions", activityId, completedActions.Count);
                foreach (var completedAction in completedActions)
                {
                    try
                    {
                        completedAction.Invoke(ret.Log);
                        Logger.LogTrace("Activity {ActivityId} completion action executed successfully", activityId);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error executing completion action for activity {ActivityId}", activityId);
                    }
                }
                
                // Signal completion
                Logger.LogInformation("Activity {ActivityId} signaling TaskCompletionSource with final status {FinalStatus}", 
                    activityId, finalStatus);
                var setResult = _completionSource.TrySetResult(ret.Log);
                Logger.LogDebug("Activity {ActivityId} TaskCompletionSource.TrySetResult returned {SetResult}, Task status: {TaskStatus}", 
                    activityId, setResult, _completionSource.Task.Status);
                
                return ret;
            }
            else
            {
                Logger.LogWarning("Activity {ActivityId} CompleteMyself called but status is not Running: {CurrentStatus}", 
                    activityId, a.Log.Status);
            }

            return a;
        }, ex => 
        {
            // If Update fails, signal the completion with an exception
            Logger.LogError(ex, "Activity {ActivityId} Update failed in CompleteMyself, signaling exception", activityId);
            _completionSource.TrySetException(ex);
            return FailActivity(ex);
        });
        
        Logger.LogInformation("Activity {ActivityId} CompleteMyself finished", activityId);
    }

    private readonly List<Action<ActivityLog>> completedActions = new();
    private readonly TaskCompletionSource<ActivityLog> _completionSource = new();

    /// <summary>
    /// Task that completes when the activity is finished, returning the final ActivityLog
    /// </summary>
    public Task<ActivityLog> Completion => _completionSource.Task;

    public void Complete(Action<ActivityLog>? completedAction = null, ActivityStatus? status = null)
    {
        var activityId = Id ?? Guid.NewGuid().ToString("N")[..8];
        Logger.LogInformation("Activity {ActivityId} Complete() called with status {Status}, SubActivities: {SubActivityCount}, CurrentStatus: {CurrentStatus}", 
            activityId, status, Log.SubActivities.Count, Log.Status);

        if (completedAction != null)
        {
            completedActions.Add(completedAction);
            Logger.LogDebug("Activity {ActivityId} added completion action, total actions: {ActionCount}", activityId, completedActions.Count);
        }

        // Direct call to avoid any threading issues - this should work since ProcessActivityCompletion is designed to be non-blocking
        Logger.LogDebug("Activity {ActivityId} calling ProcessActivityCompletion directly", activityId);
        ProcessActivityCompletion(activityId, status);

        Logger.LogInformation("Activity {ActivityId} Complete() finished processing, TaskCompletion status: {TaskStatus}", 
            activityId, _completionSource.Task.Status);
    }

    public IMessageDelivery HandleMessage(IMessageDelivery<ProcessActivityCompletionRequest> delivery)
    {
        var request = delivery.Message;
        ProcessActivityCompletion(request.ActivityId, request.Status);
        return delivery.Processed();
    }

    public IMessageDelivery HandleMessage(IMessageDelivery<CompleteActivitySelfRequest> delivery)
    {
        var request = delivery.Message;
        Logger.LogInformation("Activity {ActivityId} received self-completion request with status {Status}", 
                             request.ActivityId, request.Status);
        CompleteMyself(request.Status);
        return delivery.Processed();
    }

    private void ProcessActivityCompletion(string activityId, ActivityStatus? status)
    {
        Logger.LogInformation("Activity {ActivityId} processing completion with status {Status}, CurrentStatus: {CurrentStatus}, SubActivities: {SubActivityCount}", 
            activityId, status, Log.Status, Log.SubActivities.Count);

        // Check if we should complete immediately (no sub-activities or all sub-activities finished)
        if (Log.SubActivities.Count == 0 && Log.Status == ActivityStatus.Running)
        {
            Logger.LogInformation("Activity {ActivityId} has no sub-activities, completing immediately", activityId);
            CompleteMyself(status);
            return;
        }

        // If we have sub-activities, set up stream subscription for auto-completion
        if (Log.SubActivities.Count > 0 && Log.Status == ActivityStatus.Running)
        {
            Logger.LogInformation("Activity {ActivityId} has {SubActivityCount} sub-activities, setting up auto-completion stream", 
                                 activityId, Log.SubActivities.Count);
            
            // Log status of all sub-activities
            foreach (var (subId, subActivity) in Log.SubActivities)
            {
                Logger.LogDebug("Activity {ActivityId} sub-activity {SubId} status: {SubStatus}", 
                    activityId, subId, subActivity.Status);
            }
            
            // Subscribe to stream for auto-completion when all sub-activities finish
            var subscription = Stream
                .Where(x => ReferenceEquals(x, this) &&
                           x.Log.Status == ActivityStatus.Running &&
                           (x.Log.SubActivities.Count == 0 ||
                            x.Log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running)))
                .Subscribe(a =>
                {
                    Logger.LogInformation("Activity {ActivityId} auto-completion stream triggered, Status: {Status}, SubActivities: {SubCount}, Thread: {ThreadId}",
                        activityId, a.Log.Status, a.Log.SubActivities.Count, Thread.CurrentThread.ManagedThreadId);
                    
                    // Route completion through the hub instead of calling directly
                    Hub.Post(new CompleteActivitySelfRequest(activityId, status),
                           options => options.WithTarget(Hub.Address));
                });

            // Register subscription for cleanup
            Hub.RegisterForDisposal(_ => subscription.Dispose());
            Logger.LogDebug("Activity {ActivityId} stream subscription registered and disposal handler added", activityId);
        }
        else if (Log.Status == ActivityStatus.Running)
        {
            // Complete immediately if ready
            Logger.LogInformation("Activity {ActivityId} completing immediately (no pending sub-activities)", activityId);
            CompleteMyself(status);
        }
        else
        {
            Logger.LogWarning("Activity {ActivityId} not in Running status ({CurrentStatus}), skipping completion", activityId, Log.Status);
        }

        Logger.LogInformation("Activity {ActivityId} completion processing finished, Thread: {ThreadId}", activityId, Thread.CurrentThread.ManagedThreadId);
    }
}

