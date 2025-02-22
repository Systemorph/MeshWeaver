using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities;

public abstract record ActivityBase: IDisposable
{

    protected readonly ILogger Logger;

    protected ActivityBase(string category, IMessageHub hub)
    {
        this.Hub = hub;
        this.Logger = hub.ServiceProvider.GetRequiredService<ILogger<Activity>>();
        Log = new(category);
        SyncHub = hub.GetHostedHub(new ActivityAddress(), x => x);
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
    protected readonly IMessageHub Hub;

    public void Dispose()
    {
        lock (disposeLock)
        {
            if(isDisposed)
                return;
            isDisposed = true;
        }

        foreach (var disposable in Disposables)
            disposable.Dispose();
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

    private TActivity current;

    public void Update(Func<TActivity, TActivity> update, Action<Exception> exceptionCallback)
    {
        SyncHub.InvokeAsync(() =>
        {
            current = update.Invoke(current);
            Stream.OnNext(current);
        }, exceptionCallback);
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
    private TaskCompletionSource taskCompletionSource ;
    public Task Complete(Action<ActivityLog> completedAction = null, ActivityStatus? status = null, CancellationToken cancellationToken = default)
    {
        if (completedAction != null)
            completedActions.Add(completedAction);
        lock (completionLock)
        {
            if (taskCompletionSource != null)
                return taskCompletionSource.Task;

            taskCompletionSource = new(cancellationToken);

            Stream.Where(x => x.Log.SubActivities.Count == 0 || x.Log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running))
                .Subscribe(a =>
                {
                    if(a.Log.Status == ActivityStatus.Running)
                        CompleteMyself(null);
                });
            return taskCompletionSource.Task;
        }
    }

}

