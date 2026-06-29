using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// A unit of work that accumulates progress, log messages and sub-activities into an
/// <see cref="ActivityLog"/>. Each activity owns a hosted hub so every mutation is serialised on a
/// single action block, and it implements <see cref="ILogger"/> so standard logging is captured.
/// </summary>
public class Activity : ILogger, IDisposable
{
    private readonly ILogger logger;
    private bool autoClose;

    /// <summary>
    /// Creates an activity hosted under <paramref name="parentHub"/>, inheriting the parent's posting
    /// identity so the activity hub's own posts resolve the same way.
    /// </summary>
    /// <param name="category">Category label recorded on the activity log.</param>
    /// <param name="parentHub">Hub that hosts this activity and supplies the posting identity.</param>
    /// <param name="autoClose">When true, the activity completes automatically once all sub-activities finish.</param>
    public Activity(string category, IMessageHub parentHub, bool autoClose = true)
    {
        Category = category;
        ParentHub = parentHub ?? throw new ArgumentNullException(nameof(parentHub));
        Id = Guid.NewGuid().AsString();
        Address = AddressExtensions.CreateActivityAddress(Id);
        // Inherit the parent hub's posting identity so the activity hub's own otherwise-
        // unattributed posts resolve the same way (feedback_access_context_always_set). In prod the
        // parent is a User hub (= the default), so this is a no-op; in plumbing tests the parent is a
        // System hub and the activity hub must be System too, else its UpdateStreamRequest/Execution
        // posts have no AccessContext and the never-null guard fails them closed.
        Hub = parentHub.GetHostedHub(Address,
            conf => conf.WithPostingIdentity(parentHub.Configuration.PostingIdentity));
        logger = Hub.ServiceProvider.GetRequiredService<ILogger<Activity>>();
        this.autoClose = autoClose;
        activityLog = new(category) { StartVersion = (int)parentHub.Version };
    }

    /// <summary>The hosted hub that serialises all mutation of this activity.</summary>
    public IMessageHub Hub { get; }
    private IMessageHub ParentHub { get; }

    /// <summary>Category label that classifies this activity in its log.</summary>
    public string Category { get; }

    /// <summary>Unique identifier of this activity.</summary>
    public string Id { get; }
    /// <summary>Mesh address of this activity's hosted hub.</summary>
    public Address Address { get; }

    /// <summary>Child activities started under this one; their completion gates this activity's own completion.</summary>
    public ConcurrentBag<Activity> SubActivities { get; } = new();

    /// <summary>
    /// Completes the activity with the given status, scheduling completion on the activity hub's action block.
    /// </summary>
    /// <param name="activityStatus">Terminal status to apply, or null to derive it from the log and sub-activities.</param>
    /// <param name="completeAction">Optional callback invoked with the final log once completion settles.</param>
    public void Complete(ActivityStatus? activityStatus, Action<ActivityLog>? completeAction)
        => Hub.InvokeAsync(() => HandleComplete(activityStatus, completeAction));

    /// <summary>Completes the activity, deriving the terminal status automatically.</summary>
    /// <param name="completeAction">Optional callback invoked with the final log once completion settles.</param>
    public void Complete(Action<ActivityLog>? completeAction = null)
        => Complete(null, completeAction);


    /// <summary>Records an error-level message on the activity log.</summary>
    /// <param name="message">Message text.</param>
    /// <param name="scopes">Optional structured scopes attached to the message.</param>
    public void LogError(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Error, scopes);
    /// <summary>Records a warning-level message on the activity log.</summary>
    /// <param name="message">Message text.</param>
    /// <param name="scopes">Optional structured scopes attached to the message.</param>
    public void LogWarning(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Warning, scopes);
    /// <summary>Records an information-level message on the activity log.</summary>
    /// <param name="message">Message text.</param>
    /// <param name="scopes">Optional structured scopes attached to the message.</param>
    public void LogInformation(string message, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => LogMessage(message, LogLevel.Information, scopes);

    /// <summary>Records a message at the given level on the activity log.</summary>
    /// <param name="message">Message text.</param>
    /// <param name="logLevel">Severity of the message.</param>
    /// <param name="scopes">Optional structured scopes attached to the message.</param>
    public void LogMessage(string message, LogLevel logLevel, IReadOnlyCollection<KeyValuePair<string, object>>? scopes = null)
        => MutateLog(log => log with { Messages = log.Messages.Add(new LogMessage(message, logLevel) { Scopes = scopes }) });

    /// <summary>
    /// Reactive snapshot of the current <see cref="ActivityLog"/>, read on the activity hub's
    /// action block (so it's consistent with concurrent mutations) and delivered to the
    /// subscriber. No Task surface — subscribe / compose, never await on a hub thread.
    /// </summary>
    public IObservable<ActivityLog> GetLog() =>
        Observable.Create<ActivityLog>(observer =>
        {
            Hub.InvokeAsync(() =>
            {
                observer.OnNext(activityLog);
                observer.OnCompleted();
            });
            return System.Reactive.Disposables.Disposable.Empty;
        });

    /// <summary>Adds mesh node paths that this activity changed to the log's affected-paths set.</summary>
    /// <param name="paths">Paths affected by the activity.</param>
    public void RecordAffectedPaths(IEnumerable<string> paths)
        => MutateLog(log => log with { AffectedPaths = log.AffectedPaths.AddRange(paths) });

    /// <summary>
    /// <see cref="ILogger"/> entry point: formats <paramref name="state"/> and records it as a message on the log.
    /// </summary>
    /// <typeparam name="TState">Type of the state object being logged.</typeparam>
    /// <param name="logLevel">Severity of the entry.</param>
    /// <param name="eventId">Event identifier (part of the logger contract).</param>
    /// <param name="state">State to log.</param>
    /// <param name="exception">Optional exception associated with the entry.</param>
    /// <param name="formatter">Function that renders <paramref name="state"/> and <paramref name="exception"/> to text.</param>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => MutateLog(log => log with { Messages = log.Messages.Add(new LogMessage(formatter.Invoke(state, exception), logLevel)) });

    /// <summary>Returns whether the given log level is enabled on the underlying logger.</summary>
    /// <param name="logLevel">Level to test.</param>
    /// <returns>True if the level is enabled.</returns>
    public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);

    /// <summary>Begins a logical logging scope on the underlying logger.</summary>
    /// <typeparam name="TState">Type of the scope state.</typeparam>
    /// <param name="state">Scope state.</param>
    /// <returns>A disposable that ends the scope, or null.</returns>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

    /// <summary>
    /// Starts a child activity whose log is merged into this activity's log; when all sub-activities
    /// finish and auto-close is set, this activity completes.
    /// </summary>
    /// <param name="category">Category label for the sub-activity.</param>
    /// <returns>The newly started sub-activity.</returns>
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

    /// <summary>Disposes the activity's hosted hub unless the hub is already tearing down hosted hubs.</summary>
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
            SignalFaulted(ex);
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
                SignalCompleted(log);
            }
            else
            {
                // Signal completion with current log
                SignalCompleted(log);
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
            SignalFaulted(ex);
        }
        finally
        {
            Hub.Dispose();
        }
        return log;
    }

    private readonly ConcurrentBag<Action<ActivityLog>> completedActions = new();
    // 100% reactive completion. AsyncSubject replays the single final ActivityLog (then
    // completes) to every subscriber — including late ones — exactly like a Task<T> would,
    // but OnNext fires synchronously on the activity hub's action block (where Complete runs),
    // never hopping to the TaskScheduler. NO TaskCompletionSource: a Task surface invites
    // `await activity.Completion` on a hub action block, which deadlocks (see
    // Doc/Architecture/AsynchronousCalls.md). All mutation of the fields below happens on the
    // action block (Complete → InvokeAsync → HandleComplete / MutateLog), so they need no lock.
    private readonly AsyncSubject<ActivityLog> completionSubject = new();
    private bool isTerminal;
    private ActivityLog? finalLog;

    private void SignalCompleted(ActivityLog log)
    {
        if (isTerminal) return;          // first-wins, mirrors TaskCompletionSource.TrySetResult
        isTerminal = true;
        finalLog = log;
        completionSubject.OnNext(log);
        completionSubject.OnCompleted();
    }

    private void SignalFaulted(Exception ex)
    {
        if (isTerminal) return;
        isTerminal = true;
        completionSubject.OnError(ex);
    }

    /// <summary>
    /// Reactive completion — emits the final <see cref="ActivityLog"/> once the activity
    /// finishes, then completes. Subscribe (compose with .SelectMany/.Subscribe); never bridge
    /// to a Task and await on a hub action block. Tests bridge at their edge with
    /// <c>.FirstAsync().ToTask()</c>.
    /// </summary>
    public IObservable<ActivityLog> Completion => completionSubject.AsObservable();


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
            // If already completed, invoke callback immediately with the final log. Runs on
            // the action block, so reading isTerminal/finalLog is race-free (no Task.Result).
            if (isTerminal)
            {
                if (finalLog is not null)
                    completeAction.Invoke(finalLog);
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
