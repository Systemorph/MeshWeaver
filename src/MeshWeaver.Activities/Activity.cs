using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities
{
    public record Activity : ILogger
    {
        public Activity(string category, IMessageHub hub) 
        {
            Log = new(category);
            ActivityAddress = new ActivityAddress(Log.Id);
            this.Hub = hub.GetHostedHub(ActivityAddress, 
                x => x.WithHandler<CompleteActivityRequest>((_,request)=>HandleCompleteRequest(request))
                    .WithHandler<LogRequest>((_, request) => HandleLogRequest(request))
                    .WithHandler<StartSubActivityRequest>((_, request) => HandleStartSubActivityRequest(request))
                );
            this.Logger = Hub.ServiceProvider.GetRequiredService<ILogger<Activity>>();

            current = this;
            Stream.OnNext(current);
        }

        protected void CompleteMyself(ActivityStatus? status)
        {
            if (current.Log.Status == ActivityStatus.Running)
            {
                var finalStatus = status ?? (current.HasErrors() 
                    ? ActivityStatus.Failed 
                    : current.HasWarnings() 
                        ? ActivityStatus.Warning
                    : ActivityStatus.Succeeded);
                current = current.WithLog(log => log with
                {
                    Status = finalStatus,
                    End = DateTime.UtcNow,
                    Version = log.Version + 1
                });

            }
            while (completedActions.TryTake(out var completedAction))
            {
                try
                {
                    completedAction.Invoke(current.Log);
                }
                catch (Exception ex)
                {
                    // Silently handle completion action errors
                    Logger.LogWarning("Exception during completion of Activity {activityId}:\n{exception}", Hub.Address.Id, ex);
                }

            }

            // Signal completion
            _completionSource.TrySetResult(current.Log);


        }

        private readonly ConcurrentBag<Action<ActivityLog>> completedActions = new();
        private readonly TaskCompletionSource<ActivityLog> _completionSource = new();

        /// <summary>
        /// Task that completes when the activity is finished, returning the final ActivityLog
        /// </summary>
        public Task<ActivityLog> Completion => _completionSource.Task;

        public void Complete(Action<ActivityLog>? completedAction = null, ActivityStatus? status = null)
        {

            if (completedAction != null)
            {
                completedActions.Add(completedAction);
            }

            // Direct call to avoid any threading issues - this should work since ProcessActivityCompletion is designed to be non-blocking
            Hub.Post(new CompleteActivityRequest(status));
        }


        public IMessageDelivery HandleCompleteRequest(IMessageDelivery<CompleteActivityRequest> delivery)
        {
            var request = delivery.Message;
            ProcessActivityCompletion(request.Status);
            return delivery.Processed();
        }

        private void ProcessActivityCompletion(ActivityStatus? status)
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
                        Hub.Post(new CompleteActivityRequest(status),
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

        public IMessageDelivery HandleLogRequest(IMessageDelivery<LogRequest> request)
        {
            current = current.WithLogs(request.Message.LogMessages);
            return request.Processed();
        }

        private Activity WithLogs(IReadOnlyCollection<LogMessage> logMessages)
        {
            return this.WithLog(log => log with
            {
                Messages = log.Messages.AddRange(logMessages),
                Version = log.Version + 1,
                Status = GetMax(log.Status, logMessages)
            });
        }

        private ActivityStatus GetMax(ActivityStatus status, IReadOnlyCollection<LogMessage> logMessages)
        {
            var maxStatus = logMessages.Max(m => m.LogLevel);
            var messageStatus = maxStatus switch
            {
                LogLevel.Critical or LogLevel.Error => ActivityStatus.Failed,
                LogLevel.Warning => ActivityStatus.Warning,
                _ => ActivityStatus.Running
            };

            return (ActivityStatus)Math.Max((int)status, (int)messageStatus);
        }

        protected ReplaySubject<Activity> Stream { get; } = new(1);



        protected Activity WithLog(Func<ActivityLog, ActivityLog> update)
            => this with { Log = update.Invoke(Log) };

        protected Task FailActivity(Exception ex)
        {
            current =  current with { Log = Log.Fail($"An exception occurred: {ex}") };
            return Task.CompletedTask;
        }

        private Activity current;

        //public void Update(Func<Activity, Activity> update, Func<Exception, Task> exceptionCallback)
        //{

        //    // Check if hub is disposing to avoid hanging
        //    if (Hub.IsDisposing)
        //    {
        //        exceptionCallback.Invoke(new InvalidOperationException("Hub is disposing, cannot process update."));
        //        return;
        //    }

        //    try
        //    {
        //        Hub.InvokeAsync(() =>
        //        {
        //            current = update.Invoke(current);
        //        }, exceptionCallback);
        //    }
        //    catch (Exception ex)
        //    {
        //        exceptionCallback.Invoke(ex);
        //    }
        //}

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


        protected void LogMessage(LogMessage item)
        {
            // Send LogRequest to handle logging
            Hub.Post(new LogRequest(ActivityAddress, item));
            current = current.WithLog(log => log with { Messages = log.Messages.Add(item), Version = log.Version + 1 });
        }





        private IMessageDelivery HandleStartSubActivityRequest(IMessageDelivery<StartSubActivityRequest> request)
        {
            var subActivity = new Activity(request.Message.Category, Hub);
            current = current.WithLog(l =>
                l with { SubActivities = l.SubActivities.SetItem(subActivity.Id, subActivity.Log) });
            subActivity.Stream.Skip(1).Subscribe(sa =>
                Hub.InvokeAsync(() => current = current.WithLog(
                        log => log with { SubActivities = log.SubActivities.SetItem(sa.Id, sa.Log), Version = log.Version + 1 })
                    , FailActivity)
            );
            return request.Processed();
        }

        public void StartSubActivity(string category)
        {
            Hub.Post(new StartSubActivityRequest(category));
        }
        protected readonly ILogger Logger;

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
