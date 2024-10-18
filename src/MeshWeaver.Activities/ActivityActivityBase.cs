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
        SyncHub = hub.GetHostedHub(new object(), x => x);
    }

    protected readonly IMessageHub SyncHub;
    public string Id => Log.Id;
    protected ActivityLog Log { get; init; }
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
    {
        return This with { Log = update.Invoke(Log) };
    }
    protected ActivityBase(string category, IMessageHub hub) : base(category, hub)
    {
        current = (TActivity)this;
        Update(x => x);
    }

    private TActivity current;

    protected void Update(Func<TActivity, TActivity> update)
    {
        SyncHub.InvokeAsync(() =>
        {
            current = update.Invoke(current);
            Stream.OnNext(current);
        });
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
            )
        );
    }

    public void ChangeStatus(ActivityStatus status)
    {
        Update(x => x.WithLog(log => log with
                {
                    Status = status,
                    Version = x.Log.Version + 1
                }
            )
        );
    }







    public Activity StartSubActivity(string category)
    {
        var subActivity = new Activity(category, Hub);
        Update(x => x.WithLog(l => l with{SubActivities = l.SubActivities.SetItem(subActivity.Id, subActivity.Log)}));
        subActivity.Stream.Skip(1).Subscribe(sa =>
            Update(x => x.WithLog(
                log => log with { SubActivities = log.SubActivities.SetItem(sa.Id, sa.Log), Version = log.Version + 1 })
            )
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


public record Activity : ActivityBase<Activity>
{
    public Activity(string category, IMessageHub hub) : base(category, hub)
    {
    }



    protected void CompleteMyself()
    {
        Update(a =>
        {
            if (a.Log.Status == ActivityStatus.Running)
            {
                var ret = a.WithLog(log => log with
                {
                    Status = HasErrors() ? ActivityStatus.Failed : ActivityStatus.Succeeded,
                    End = DateTime.UtcNow,
                    Version = log.Version + 1
                });
                foreach (var completedAction in completedActions)
                    completedAction.Invoke(ret.Log);
                return ret;
            }

            return a;
        });
    }

    private readonly List<Action<ActivityLog>> completedActions = new();
    private bool isSubscribed;
    private readonly object completionLock = new();
    public void Complete(Action<ActivityLog> completedAction = null)
    {
        if(completedAction != null)
            completedActions.Add(completedAction);
        lock (completionLock)
        {
            if (isSubscribed)
                return;
            isSubscribed = true;

            Stream.Where(x => x.Log.SubActivities.Count == 0 || x.Log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running))
                .Subscribe(a =>
                {
                    if(a.Log.Status == ActivityStatus.Running)
                        CompleteMyself();
                });
        }
    }


}

//public record Activity<TResult> : ActivityBase<Activity<TResult>>
//{
//    public Activity(string category, IMessageHub hub) : base(category, hub)
//    {
//    }
//    protected void CompleteMyself(Action<ActivityLog> completedAction)
//    {
//        Update(a =>
//        {
//            var ret = a.WithLog(log => log with
//            {
//                Status = HasErrors() ? ActivityStatus.Failed : ActivityStatus.Succeeded,
//                End = DateTime.UtcNow,
//                Version = log.Version + 1
//            });
//            completedAction.Invoke(ret.Log);
//            return ret;
//        });
//    }

//    public void Complete(Action<TResult, ActivityLog> onCompleted)
//    {
//        Stream.Where(x => x.Log.SubActivities.Count == 0 || x.Log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running))
//            .Subscribe(a =>
//            {
//                if (a.Log.Status == ActivityStatus.Running)
//                {
//                    CompleteMyself(completedAction);
//                }
//            });
//    }

//    public void Complete(TResult result)
//    {
//        ChangeStatus(HasErrors() ? ActivityStatus.Failed : ActivityStatus.Succeeded);
//    }

//}
