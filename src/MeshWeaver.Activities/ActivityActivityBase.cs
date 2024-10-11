using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities;

public abstract record ActivityBase: IDisposable
{
    protected readonly ILogger Logger;
    protected ImmutableDictionary<string, ActivityBase> SubActivities { get; init; } 
        = ImmutableDictionary<string, ActivityBase>.Empty;
    protected ActivityBase(string category, ILogger logger)
    {
        this.Logger = logger;
        ActivityLog = new(category);
    }
    public string Id => ActivityLog.Id;
    public ActivityLog ActivityLog { get; init; }
    public bool IsEnabled(LogLevel logLevel) => Logger.IsEnabled(logLevel);

    public IDisposable BeginScope<TState>(TState state) => Logger.BeginScope(state);

    public bool HasErrors() => ActivityLog.Errors().Any();

    public bool HasWarnings() => ActivityLog.Warnings().Any();


    protected readonly ImmutableList<IDisposable> Disposables = [];
    private bool isDisposed = false;
    private readonly object disposeLock = new();
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

    protected IObservable<TActivity> Stream { get; }
    protected readonly Subject<Func<TActivity, TActivity>> Updates = new();

    protected ActivityBase(string category, ILogger logger) : base(category, logger)
    {
        var current = (TActivity)this;
        var stream = new ReplaySubject<TActivity>();
        Stream = stream;
        Updates.Select(update => current = update.Invoke(current)).Subscribe(stream);
        Updates.OnNext(x => x);
    }
    public void Log<TState>(
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
        Updates.OnNext(x => x with { ActivityLog = ActivityLog with { Messages = ActivityLog.Messages.Add(item) } });
    }

    public void ChangeStatus(string status)
    {
        Updates.OnNext(x => x with { ActivityLog = ActivityLog with { Status = status } });
    }





    public void OnFinished(Action<ActivityLog> func)
    {
        Stream.Where(x => x.ActivityLog.Status != ActivityLogStatus.Running)
            .Subscribe(a => func.Invoke(a.ActivityLog));
    }


    public Activity Start(string category, string message)
    {
        var subActivity = new Activity(category, Logger);
        subActivity.Stream.Subscribe(sa =>
            Updates.OnNext(x => x with { SubActivities = SubActivities.SetItem(sa.Id, sa) })
        );
        return subActivity;
    }
    public Activity<TResult> Start<TResult>(string category)
    {
        var subActivity = new Activity<TResult>(category, Logger);
        subActivity.Stream.Subscribe(sa =>
            Updates.OnNext(x => x with { SubActivities = SubActivities.SetItem(sa.Id, sa) })
        );
        return subActivity;
    }

}


public record Activity : ActivityBase<Activity>
{
    public Activity(string category, ILogger logger) : base(category, logger)
    {
    }


    public void Finish()
    {
        Updates.OnNext(a => a with{ActivityLog = a.ActivityLog with {Status = HasErrors() ? ActivityLogStatus.Failed : ActivityLogStatus.Succeeded, End = DateTime.UtcNow}});
    }
    public void FinishOnCompleteSubActivities()
    {
        Stream.Where(x => x.SubActivities.Values.All(y => y.ActivityLog.Status != ActivityLogStatus.Running))
            .Subscribe(a =>
            {
                if (ActivityLog.Status == ActivityLogStatus.Running)
                {
                    Finish();
                }
            });
    }


}

public record Activity<TResult> : ActivityBase<Activity<TResult>>
{
    public Activity(string category, ILogger logger) : base(category, logger)
    {
    }

    public void OnCompleted(Action<TResult, ActivityLog> onCompleted)
    {
        OnFinished(log =>
        {
            onCompleted(default, log);
        });
    }

    public (TResult Result, ActivityLog Log) Finish(TResult result)
    {
        if (this.ActivityLog == null)
            return (result, null);

        if (HasErrors())
            ChangeStatus(ActivityLogStatus.Failed);
        else
            ChangeStatus(ActivityLogStatus.Succeeded);

        return (result,ActivityLog with { End = DateTime.UtcNow });
    }

}
