using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities;

public abstract record ActivityBase: IDisposable
{

    protected readonly ILogger Logger;    protected ActivityBase(string category, IMessageHub hub)
    {
        this.Hub = hub;
        this.Logger = hub.ServiceProvider.GetRequiredService<ILogger<Activity>>();
        Log = new(category);
        
        try
        {
            SyncHub = hub.GetHostedHub(new ActivityAddress(), x => x);
            if (SyncHub == null)
            {
                Logger.LogWarning("GetHostedHub returned null for ActivityAddress in activity {category}. Check if ActivityAddress is registered in address types.", category);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create SyncHub for activity {category}", category);
            throw;
        }
    }

    protected readonly IMessageHub SyncHub;
    public string Id => Log.Id;
    public ActivityLog Log { get; init; }
    public bool IsEnabled(LogLevel logLevel) => Logger.IsEnabled(logLevel);

    public IDisposable BeginScope<TState>(TState state) => Logger.BeginScope(state);

    public bool HasErrors() => Log.Errors().Any();

    public bool HasWarnings() => Log.Warnings().Any();


    protected readonly ImmutableList<IDisposable> Disposables = [];
    private bool isDisposed;
    private readonly object disposeLock = new();
    protected readonly IMessageHub Hub;    public void Dispose()
    {
        lock (disposeLock)
        {
            if(isDisposed)
                return;
            isDisposed = true;
        }

        foreach (var disposable in Disposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error disposing resource in activity {ActivityId}", Id);
            }
        }
    }


}
public abstract record ActivityBase<TActivity> : ActivityBase, ILogger
    where TActivity:ActivityBase<TActivity>
{

    protected ReplaySubject<TActivity> Stream { get; } = new(1);



    protected TActivity WithLog(Func<ActivityLog, ActivityLog> update)
        => This with { Log = update.Invoke(Log) };
    protected ActivityBase(string category, IMessageHub hub) : base(category, hub)
    {
        current = (TActivity)this;
        Update(x => x, FailActivity);
    }

    protected void FailActivity(Exception ex)
    { 
        Update(x =>
        {
            Logger.LogWarning(ex, "An exception occurred in {Activity}", x);
            return x with { Log = Log.Fail($"An exception occurred: {ex}") };
        }, _ => { });
    }

    private TActivity current;    public void Update(Func<TActivity, TActivity> update, Action<Exception> exceptionCallback)
    {
        if (SyncHub == null)
        {
            Logger.LogWarning("SyncHub is null in Update for activity {ActivityId}. Activity may have been created after hub disposal.", Id);
            exceptionCallback?.Invoke(new InvalidOperationException("SyncHub is null. Activity was likely created after hub disposal."));
            return;
        }

        // Check if hub is disposing to avoid hanging
        if (SyncHub.IsDisposing)
        {
            Logger.LogWarning("SyncHub is disposing, skipping update for activity {ActivityId}", Id);
            exceptionCallback?.Invoke(new InvalidOperationException("SyncHub is disposing, cannot process update."));
            return;
        }

        try
        {
            SyncHub.InvokeAsync(() =>
            {
                current = update.Invoke(current);
                Stream.OnNext(current);
            }, exceptionCallback);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in Update for activity {ActivityId}", Id);
            exceptionCallback?.Invoke(ex);
        }
    }

    void ILogger.Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter
    )
    {
        Logger.Log(logLevel, eventId, state, exception, formatter);

        var item = new LogMessage(state.ToString(), logLevel);
        if (state is IReadOnlyCollection<KeyValuePair<string, object>> list)
            item = item with { Scopes = list };
        LogMessage(item);
    }


    protected TActivity This => (TActivity) this;
    protected void LogMessage(LogMessage item)
    {
        Update(x => x.WithLog(log => log with
                {
                    Messages = log.Messages.Add(item), 
                    Version = log.Version + 1
                }

            ), FailActivity
        );
    }







    public Activity StartSubActivity(string category)
    {
        var subActivity = new Activity(category, Hub);
        Update(x => x.WithLog(l => l with{SubActivities = l.SubActivities.SetItem(subActivity.Id, subActivity.Log)}), FailActivity);
        subActivity.Stream.Skip(1).Subscribe(sa =>
            Update(x => x.WithLog(
                log => log with { SubActivities = log.SubActivities.SetItem(sa.Id, sa.Log), Version = log.Version + 1 })
            , FailActivity)
        );
        return subActivity;
    }
    //public Activity<TResult> StartSubActivity<TResult>(string category)
    //{
    //    var subActivity = new Activity<TResult>(category, Hub);
    //    subActivity.Stream.Subscribe(sa =>
    //        Update(x => x.WithLog(
    //            log => log with { SubActivities = log.SubActivities.SetItem(sa.Id, sa.Log), Version = log.Version + 1 })
    //        )
    //    );
    //    return subActivity;
    //}

}


public record Activity(string category, IMessageHub hub) : ActivityBase<Activity>(category, hub)
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
        },FailActivity);
    }

    private readonly List<Action<ActivityLog>> completedActions = new();
    private readonly object completionLock = new();
    private TaskCompletionSource taskCompletionSource ;    public async Task Complete(Action<ActivityLog> completedAction = null, ActivityStatus? status = null, CancellationToken cancellationToken = default)
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
                        if(a.Log.Status == ActivityStatus.Running)
                            CompleteMyself(null);
                    });
                completionTask = taskCompletionSource.Task;
            }
        }

        // Add timeout to prevent hanging - default 10 seconds unless cancellation token has shorter timeout
        var timeout = TimeSpan.FromSeconds(10);
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

