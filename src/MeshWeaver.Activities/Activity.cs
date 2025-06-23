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
                taskCompletionSource.SetResult();
                return ret;
            }

            return a;
        }, FailActivity);
    }

    private readonly List<Action<ActivityLog>> completedActions = new();
    private readonly object completionLock = new();
    private TaskCompletionSource taskCompletionSource;
    public async Task Complete(Action<ActivityLog> completedAction = null, ActivityStatus? status = null, CancellationToken cancellationToken = default)
    {
        if (completedAction != null)
            completedActions.Add(completedAction);

        Task completionTask;
        lock (completionLock)
        {
            if (taskCompletionSource != null)
            {
                completionTask = taskCompletionSource.Task;
            }
            else
            {
                taskCompletionSource = new(cancellationToken);

                Stream.Where(x => x.Log.SubActivities.Count == 0 || x.Log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running))
                    .Subscribe(a =>
                    {
                        if (a.Log.Status == ActivityStatus.Running)
                            CompleteMyself(status);
                    });
                completionTask = taskCompletionSource.Task;
            }
        }

        // Add timeout to prevent hanging - default 10 seconds unless cancellation token has shorter timeout
        var timeout = TimeSpan.FromSeconds(100);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!cancellationToken.CanBeCanceled || cancellationToken == CancellationToken.None)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            await completionTask.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("Activity {ActivityId} completion timed out after {Timeout} seconds", Id, timeout.TotalSeconds);
            // Force completion with failed status
            CompleteMyself(ActivityStatus.Failed);
            throw new TimeoutException($"Activity {Id} completion timed out after {timeout.TotalSeconds} seconds");
        }
    }

}

