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
        Address = AddressExtensions.CreateActivityAddress(Id);
        Hub = parentHub.GetHostedHub(Address, conf => conf);
        logger = Hub.ServiceProvider.GetRequiredService<ILogger<Activity>>();
        this.autoClose = autoClose;
        activityLog = new(category) { StartVersion = (int)parentHub.Version };
    }

    public IMessageHub Hub { get; }
    private IMessageHub ParentHub { get; }

    public string Category { get; }

    public string Id { get; }
    public Address Address { get; }

    public ConcurrentBag<Activity> SubActivities { get; } = new();

    public void Complete(ActivityStatus? activityStatus, Action<ActivityLog>? completeAction)
        => Hub.InvokeAsync(() => HandleComplete(activityStatus, completeAction));

    public void Complete(Action<ActivityLog>? completeAction = null)
        => Complete(null, completeAction);


    public void LogError(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Error, scopes);
    public void LogWarning(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Warning, scopes);
    public void LogInformation(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Information, scopes);

    public void LogMessage(string message, LogLevel logLevel, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => MutateLog(log => log with { Messages = log.Messages.Add(new LogMessage(message, logLevel) { Scopes = scopes }) });

    public Task<ActivityLog> GetLogAsync()
    {
        var tcs = new TaskCompletionSource<ActivityLog>();
        Hub.InvokeAsync(() => tcs.SetResult(activityLog));
        return tcs.Task;
    }

    public void RecordAffectedPaths(IEnumerable<string> paths)
        => MutateLog(log => log with { AffectedPaths = log.AffectedPaths.AddRange(paths) });

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => MutateLog(log => log with { Messages = log.Messages.Add(new LogMessage(formatter.Invoke(state, exception), logLevel)) });

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
        MutateLog(log =>
        {
            var existing = log.SubActivities.FirstOrDefault(l => l.Id == subLog.Id);
            log = log with
            {
                SubActivities = existing is not null ? log.SubActivities.Replace(existing, subLog) : log.SubActivities.Add(subLog),
                Version = (int)Hub.Version
            };

            // Check if all sub-activities are complete — schedule completion on the
            // action block so it runs AFTER this lambda's mutation commits.
            if (autoClose && log.SubActivities.All(subAct => subAct.Status != ActivityStatus.Running))
                Hub.InvokeAsync(() => HandleComplete(null, null));
            return log;
        });
    }

    public void Dispose()
    {
        if (Hub.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            Hub.Dispose();
    }

    #region HubImplementation

    private ActivityLog activityLog;
    private event EventHandler<ActivityLog>? LogChanged;

    /// <summary>
    /// Apply <paramref name="logAction"/> to <see cref="activityLog"/> on the
    /// activity hub's action block, stamp the framework Version, and fire
    /// <see cref="LogChanged"/>. Single-threaded by construction — no lock
    /// needed because <see cref="IMessageHub.InvokeAsync(Action)"/> serialises
    /// through the hub's <c>ActionBlock</c>.
    /// </summary>
    private void MutateLog(Func<ActivityLog, ActivityLog> logAction)
        => Hub.InvokeAsync(() =>
        {
            activityLog = logAction(activityLog) with { Version = (int)Hub.Version };
            LogChanged?.Invoke(this, activityLog);
        });


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


    /// <summary>
    /// Runs on the activity hub's action block (scheduled by
    /// <see cref="Complete(ActivityStatus?, Action{ActivityLog}?)"/>).
    /// Replaces the old <c>CompleteActivityRequest</c> handler: the closure
    /// is captured by <see cref="IMessageHub.InvokeAsync(Action)"/>, no
    /// typed request message needed.
    /// </summary>
    private void HandleComplete(ActivityStatus? status, Action<ActivityLog>? completeAction)
    {
        if (completeAction != null)
        {
            // If already completed, invoke callback immediately with the final log
            if (completionSource.Task.IsCompleted)
            {
                completeAction.Invoke(completionSource.Task.Result);
                return;
            }
            completedActions.Add(completeAction);
        }
        MutateLog(log =>
        {
            var ret = ProcessActivityCompletion(log, status);
            if (activityLog.Status > ActivityStatus.Running)
                Hub.Dispose();
            return ret;
        });
    }





    #endregion
}
