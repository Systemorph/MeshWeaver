using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Activities;


public record CompleteActivityRequest(string ActivityId, ActivityStatus? Status) : IRequest;

public record Activity(string Category, IMessageHub Hub) : ActivityBase<Activity>(Category, Hub),
    IMessageHandler<CompleteActivityRequest>
{
    protected void CompleteMyself(ActivityStatus? status)
    {

        Update(a =>
        {
            if (a.Log.Status == ActivityStatus.Running)
            {
                var finalStatus = status ?? (a.HasErrors() ? ActivityStatus.Failed : ActivityStatus.Succeeded);
                var ret = a.WithLog(log => log with
                {
                    Status = finalStatus,
                    End = DateTime.UtcNow,
                    Version = log.Version + 1
                });
                
                foreach (var completedAction in completedActions)
                {
                    try
                    {
                        completedAction.Invoke(ret.Log);
                    }
                    catch
                    {
                        // Silently handle completion action errors
                    }
                }
                
                // Signal completion
                _completionSource.TrySetResult(ret.Log);
                
                return ret;
            }
            else
            {
                // Activity not in running status, skip completion
            }

            return a;
        }, ex => 
        {
            // If Update fails, signal the completion with an exception
            _completionSource.TrySetException(ex);
            return FailActivity(ex);
        });
    }

    private readonly List<Action<ActivityLog>> completedActions = new();
    private readonly TaskCompletionSource<ActivityLog> _completionSource = new();

    /// <summary>
    /// Task that completes when the activity is finished, returning the final ActivityLog
    /// </summary>
    public Task<ActivityLog> Completion => _completionSource.Task;

    public void Complete(Action<ActivityLog>? completedAction = null, ActivityStatus? status = null)
    {
        var activityId = Id;

        if (completedAction != null)
        {
            completedActions.Add(completedAction);
        }

        // Direct call to avoid any threading issues - this should work since ProcessActivityCompletion is designed to be non-blocking
        ProcessActivityCompletion(activityId, status);
    }


    public IMessageDelivery HandleMessage(IMessageDelivery<CompleteActivityRequest> delivery)
    {
        var request = delivery.Message;
        CompleteMyself(request.Status);
        return delivery.Processed();
    }

    private void ProcessActivityCompletion(string activityId, ActivityStatus? status)
    {
        // Check if we should complete immediately (no sub-activities or all sub-activities finished)
        if (Log.SubActivities.Count == 0 && Log.Status == ActivityStatus.Running)
        {
            CompleteMyself(status);
            return;
        }

        // If we have sub-activities, set up stream subscription for auto-completion
        if (Log.SubActivities.Count > 0 && Log.Status == ActivityStatus.Running)
        {
            
            // Subscribe to stream for auto-completion when all sub-activities finish
            var subscription = Stream
                .Where(x => ReferenceEquals(x, this) &&
                           x.Log.Status == ActivityStatus.Running &&
                           (x.Log.SubActivities.Count == 0 ||
                            x.Log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running)))
                .Subscribe(_ =>
                {
                    // Route completion through the hub instead of calling directly
                    Hub.Post(new CompleteActivityRequest(activityId, status),
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
    }
}

