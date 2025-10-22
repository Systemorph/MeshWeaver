using System.Collections.Concurrent;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public class Activity : ILogger, IDisposable
{
    private readonly ILogger logger;
    private bool autoClose;

    public Activity(string category, IMessageHub parentHub, bool autoClose = true)
    {
        Category = category;
        ParentHub = parentHub ?? throw new ArgumentNullException(nameof(parentHub));
        Id = Guid.NewGuid().AsString();
        Address = new ActivityAddress(Id);
        Hub = parentHub.GetHostedHub(Address, conf => ConfigureActivityHub(this, conf));
        logger = Hub.ServiceProvider.GetRequiredService<ILogger<Activity>>();
        this.autoClose = autoClose;
        activityLog = new(category);
    }

    public IMessageHub Hub { get; }
    private IMessageHub ParentHub { get; }

    public string Category { get; }

    public string Id { get; }
    public ActivityAddress Address { get; }

    public ConcurrentBag<Activity> SubActivities { get; } = new();

    public void Complete(ActivityStatus? activityStatus, Action<ActivityLog>? completeAction)
    {
        Hub.Post(new CompleteActivityRequest(activityStatus) { CompleteAction = completeAction });
    }

    public void Complete(Action<ActivityLog>? completeAction = null)
        => Complete(null, completeAction);


    public void LogError(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Error, scopes);
    public void LogWarning(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Warning, scopes);
    public void LogInformation(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Information, scopes);

    public void LogMessage(string message, LogLevel logLevel, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
    {
        Hub.Post(new UpdateActivityLogRequest(log => log with { Messages = log.Messages.Add(new LogMessage(message, logLevel) { Scopes = scopes }) }));
    }

    public Task<ActivityLog> GetLogAsync()
    {
        var tcs = new TaskCompletionSource<ActivityLog>();
        Hub.InvokeAsync(() => tcs.SetResult(activityLog));
        return tcs.Task;
    }


    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Hub.Post(new UpdateActivityLogRequest(log => log with { Messages = log.Messages.Add(new LogMessage(formatter.Invoke(state, exception), logLevel)) }));
    }

    public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

    public Activity StartSubActivity(string category)
    {
        var subActivity = new Activity(category, ParentHub);
        Hub.RegisterForDisposal(subActivity);
        SubActivities.Add(subActivity);
        UpdateSubActivity(subActivity, subActivity.activityLog);
        // Monitor sub-activity completion using simple stream subscription with timeout
        subActivity.LogChanged += UpdateSubActivity;
        return subActivity;
    }

    private void UpdateSubActivity(object? sender, ActivityLog subLog)
    {
        Hub.Post(new UpdateActivityLogRequest(log =>
        {
            var existing = log.SubActivities.FirstOrDefault(l => l.Id == subLog.Id);
            log = log with
            {
                SubActivities = existing is not null ? log.SubActivities.Replace(existing, subLog) : log.SubActivities.Add(subLog),
                Version = (int)Hub.Version
            };

            // Check if all sub-activities are complete
            if (autoClose && log.SubActivities.All(subAct => subAct.Status != ActivityStatus.Running))
                Hub.Post(new CompleteActivityRequest(null), options => options.WithTarget(Hub.Address));
            return log;
        }));
    }

    public void Dispose()
    {
        if (Hub.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            Hub.Dispose();
    }

    #region HubImplementation

    private ActivityLog activityLog;
    private event EventHandler<ActivityLog>? LogChanged;
    internal static MessageHubConfiguration ConfigureActivityHub(Activity activity, MessageHubConfiguration configuration)
    {
        return configuration
                .WithHandler<CompleteActivityRequest>((_, request) => activity.HandleCompleteRequest(request))
                .WithHandler<UpdateActivityLogRequest>((_, request) => activity.HandleUpdateActivityLogRequest(request))
            ;
    }

    private IMessageDelivery HandleUpdateActivityLogRequest(IMessageDelivery<UpdateActivityLogRequest> request)
    {
        activityLog = request.Message.Update.Invoke(activityLog) with { Version = (int)Hub.Version };
        LogChanged?.Invoke(this, activityLog);
        return request.Processed();
    }

    private void RequestChange(Func<ActivityLog, ActivityLog> logAction)
        => Hub.Post(new UpdateActivityLogRequest(logAction));


    private ActivityLog ProcessActivityCompletion(ActivityLog log, ActivityStatus? status)
    {
        try
        {

            // Check if we should complete immediately (no sub-activities or all sub-activities finished)
            if (log.SubActivities.Count == 0)
                return log.Status == ActivityStatus.Running ? CompleteMyself(log, status) : log;

            if (log.SubActivities.Any(sa => sa.Status == ActivityStatus.Running))
            {
                autoClose = true;
                return log;
            }

            // If we have sub-activities, check if all are finished
            if (log.Status != ActivityStatus.Running)
                return log;

            // Note: Sub-activity completion monitoring is now handled through data change events
            // rather than direct stream subscriptions to avoid hanging
            // Complete immediately if ready
            return CompleteMyself(log, status);

        }
        catch (Exception ex)
        {
            logger.LogError("Error in ProcessActivityCompletion: {Exception}", ex);
            completionSource.TrySetException(ex);
            log = log.Fail(ex.ToString());
            return log;
        }
    }



    private ActivityLog CompleteMyself(ActivityLog log, ActivityStatus? status)
    {
        try
        {
            if (log.Status == ActivityStatus.Running)
            {
                log = log.Finish((int)Hub.Version, status);
                // Signal completion with updated log
                completionSource.TrySetResult(log);
            }
            else
            {
                // Signal completion with current log
                completionSource.TrySetResult(log);
            }

            while (completedActions.TryTake(out var completedAction))
            {
                try
                {
                    completedAction.Invoke(log);
                }
                catch (Exception ex)
                {
                    // Silently handle completion action errors
                    logger.LogWarning("Exception during completion of Activity {activityId}:\n{exception}",
                        Hub.Address.Id, ex);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error in CompleteMyself: {Exception}", ex);
            log = log.Fail(ex.ToString());
            completionSource.TrySetException(ex);
        }
        finally
        {
            Hub.Dispose();
        }
        return log;
    }

    private readonly ConcurrentBag<Action<ActivityLog>> completedActions = new();
    private readonly TaskCompletionSource<ActivityLog> completionSource = new();

    /// <summary>
    /// Task that completes when the activity is finished, returning the final ActivityLog
    /// </summary>
    public Task<ActivityLog> Completion => completionSource.Task;


    public IMessageDelivery HandleCompleteRequest(IMessageDelivery<CompleteActivityRequest> delivery)
    {
        var request = delivery.Message;
        if (request.CompleteAction != null)
            completedActions.Add(request.CompleteAction);
        RequestChange(log =>
        {
            var ret = ProcessActivityCompletion(log, request.Status);
            if (activityLog.Status > ActivityStatus.Running)
                Hub.Dispose();
            return ret;
        });
        return delivery.Processed();
    }





    #endregion
}
