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
        Update(a =>
        {
            if (a.Log.Status == ActivityStatus.Running)
            {
                var ret = a.WithLog(log => log with
                {
                    Status = status ?? (a.HasErrors() ? ActivityStatus.Failed : ActivityStatus.Succeeded),
                    End = DateTime.UtcNow,
                    Version = log.Version + 1
                });
                
                foreach (var completedAction in completedActions)
                    completedAction.Invoke(ret.Log);
                
                // Signal completion
                _completionSource.TrySetResult(ret.Log);
                
                return ret;
            }

            return a;
        }, FailActivity);
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
        Logger.LogInformation("Activity {ActivityId} Complete() called with status {Status}, SubActivities: {SubActivityCount}", activityId, status, Log.SubActivities.Count);

        if (completedAction != null)
            completedActions.Add(completedAction);

        // Direct call to avoid any threading issues - this should work since ProcessActivityCompletion is designed to be non-blocking
        ProcessActivityCompletion(activityId, status);

        Logger.LogInformation("Activity {ActivityId} Complete() finished processing", activityId);
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
        Logger.LogInformation("Activity {ActivityId} processing completion", activityId);

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
            Logger.LogInformation("Activity {ActivityId} has {SubActivityCount} sub-activities, setting up auto-completion", 
                                 activityId, Log.SubActivities.Count);
            
            // Subscribe to stream for auto-completion when all sub-activities finish
            var subscription = Stream
                .Where(x => ReferenceEquals(x, this) &&
                           x.Log.Status == ActivityStatus.Running &&
                           (x.Log.SubActivities.Count == 0 ||
                            x.Log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running)))
                .Subscribe(a =>
                {
                    Logger.LogInformation("Activity {ActivityId} auto-completion stream triggered, Status: {Status}, SubActivities: {SubCount}",
                        activityId, a.Log.Status, a.Log.SubActivities.Count);
                    
                    // Route completion through the hub instead of calling directly
                    Hub.Post(new CompleteActivitySelfRequest(activityId, status),
                           options => options.WithTarget(Hub.Address));
                });

            // Register subscription for cleanup
            Hub.RegisterForDisposal(_ => subscription.Dispose());
        }
        else if (Log.Status == ActivityStatus.Running)
        {
            // Complete immediately if ready
            CompleteMyself(status);
        }

        Logger.LogInformation("Activity {ActivityId} completion processing finished", activityId);
    }
}

