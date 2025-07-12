#nullable enable
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities
{
    public abstract record ActivityBase<TActivity> : ActivityBase, ILogger
        where TActivity : ActivityBase<TActivity>
    {

        protected ReplaySubject<TActivity> Stream { get; } = new(1);



        protected TActivity WithLog(Func<ActivityLog, ActivityLog> update)
            => This with { Log = update.Invoke(Log) };
        protected ActivityBase(string category, IMessageHub hub) : base(category, hub)
        {
            current = (TActivity)this;
            Update(x => x, FailActivity);
        }

        protected Task FailActivity(Exception ex)
        {
            Update(x =>
            {
                return x with { Log = Log.Fail($"An exception occurred: {ex}") };
            }, _ => Task.CompletedTask);
            return Task.CompletedTask;
        }

        private TActivity current;

        public void Update(Func<TActivity, TActivity> update, Func<Exception, Task> exceptionCallback)
        {
            if (SyncHub == null)
            {
                exceptionCallback?.Invoke(new InvalidOperationException("SyncHub is null. Activity was likely created after hub disposal."));
                return;
            }

            // Check if hub is disposing to avoid hanging
            if (SyncHub.IsDisposing)
            {
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
                exceptionCallback?.Invoke(ex);
            }
        }

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var item = new LogMessage(state?.ToString() ?? "", logLevel);
            if (state is IReadOnlyCollection<KeyValuePair<string, object>> list)
                item = item with { Scopes = list };
            LogMessage(item);
        }


        protected TActivity This => (TActivity)this;
        protected void LogMessage(LogMessage item)
        {
            // Send LogRequest to handle logging
            Hub.Post(new LogRequest(ActivityAddress, item));
            
            Update(x => x.WithLog(log => log with
            {
                Messages = log.Messages.Add(item),
                Version = log.Version + 1
            }), FailActivity);
        }







        public Activity StartSubActivity(string category)
        {
            var subActivity = new Activity(category, Hub);
            Update(x => x.WithLog(l => l with { SubActivities = l.SubActivities.SetItem(subActivity.Id, subActivity.Log) }), FailActivity);
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

    public abstract record ActivityBase : IDisposable
    {
        protected readonly ILogger Logger;

        protected ActivityBase(string category, IMessageHub hub)
        {
            this.Hub = hub;
            this.Logger = hub.ServiceProvider.GetRequiredService<ILogger<Activity>>();
            Log = new(category);
            ActivityAddress = new ActivityAddress(Log.Id);

            try
            {
                SyncHub = hub.GetHostedHub(ActivityAddress, x => x);
            }
            catch
            {
                throw;
            }
        }

        protected readonly IMessageHub? SyncHub;
        public string Id => Log.Id;
        public ActivityLog Log { get; init; }
        public ActivityAddress ActivityAddress { get; private init; }
        public bool IsEnabled(LogLevel logLevel) => Logger.IsEnabled(logLevel);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Logger.BeginScope(state);

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
                if (isDisposed)
                    return;
                isDisposed = true;
            }

            foreach (var disposable in Disposables)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Silently handle disposal errors
                }
            }
        }
    }
}
