#nullable enable
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities;

public record Activity(string Category, IMessageHub Hub) : ActivityBase<Activity>(Category, Hub)
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
                taskCompletionSource?.SetResult();
                return ret;
            }

            return a;
        }, FailActivity);
    }

    private readonly List<Action<ActivityLog>> completedActions = new();
    private readonly object completionLock = new();
    private TaskCompletionSource? taskCompletionSource; public async Task Complete(Action<ActivityLog>? completedAction = null, ActivityStatus? status = null, CancellationToken cancellationToken = default)
    {
        var activityId = Id ?? Guid.NewGuid().ToString("N")[..8];
        Logger.LogInformation("Activity {ActivityId} Complete() called with status {Status}, SubActivities: {SubActivityCount}", activityId, status, Log.SubActivities.Count);

        if (completedAction != null)
            completedActions.Add(completedAction);

        Task completionTask;
        lock (completionLock)
        {
            if (taskCompletionSource != null)
            {
                Logger.LogInformation("Activity {ActivityId} using existing TaskCompletionSource", activityId);
                completionTask = taskCompletionSource.Task;
            }
            else
            {
                Logger.LogInformation("Activity {ActivityId} creating new TaskCompletionSource and subscription", activityId);
                taskCompletionSource = new(cancellationToken);

                // Check if we should complete immediately (no sub-activities)
                if (Log.SubActivities.Count == 0 && Log.Status == ActivityStatus.Running)
                {
                    Logger.LogInformation("Activity {ActivityId} has no sub-activities, completing immediately", activityId);
                    CompleteMyself(status);
                }
                else
                {
                    // Subscribe to stream for auto-completion when all sub-activities finish
                    Stream.Where(x => ReferenceEquals(x, this) &&
                                     (x.Log.SubActivities.Count == 0 ||
                                      x.Log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running)))
                        .Subscribe(a =>
                        {
                            Logger.LogInformation("Activity {ActivityId} auto-completion stream triggered, Status: {Status}, SubActivities: {SubCount}",
                                activityId, a.Log.Status, a.Log.SubActivities.Count);
                            if (a.Log.Status == ActivityStatus.Running)
                                CompleteMyself(status);
                        });
                }
                completionTask = taskCompletionSource.Task;
            }
        }

        try
        {
            Logger.LogInformation("Activity {ActivityId} waiting for completion task", activityId);
            await completionTask.WaitAsync(cancellationToken);
            Logger.LogInformation("Activity {ActivityId} completion task finished successfully", activityId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("Activity {ActivityId} completion cancelled via cancellation token", activityId);
            // Force completion with failed status
            CompleteMyself(ActivityStatus.Failed);
            throw;
        }

        Logger.LogInformation("Activity {ActivityId} Complete() finished", activityId);
    }

}

