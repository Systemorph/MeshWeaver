using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public class Activity : ILogger, IDisposable
{
    private readonly ILogger logger;
    private readonly TaskCompletionSource<ActivityLog> completionTaskCompletionSource = new();

    public Activity(string category, IMessageHub parentHub, string? activityId = null)
    {
        Category = category;
        ParentHub = parentHub ?? throw new ArgumentNullException(nameof(parentHub));
        Id = activityId ?? Guid.NewGuid().AsString();
        Address = new ActivityAddress(Id);
        Hub = parentHub.GetHostedHub(Address, ActivityImpl.ConfigureActivityHub);
        Hub.Post(new ChangeActivityCategoryRequest(category));
        logger = Hub.ServiceProvider.GetRequiredService<ILogger<Activity>>();
        Hub.RegisterForDisposal(Hub.GetWorkspace().GetObservable<ActivityLog>()
            .Subscribe(coll =>
            {
                var activity = coll.SingleOrDefault();
                if (activity?.Status > ActivityStatus.Running)
                    completionTaskCompletionSource.TrySetResult(activity);
            })
        );
    }

    public IMessageHub Hub { get; }
    private IMessageHub ParentHub { get; }

    public string Category { get; }

    public string Id { get; } 
    public ActivityAddress Address { get; }
    public Task<ActivityLog> Completion => completionTaskCompletionSource.Task;

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

    public void LogMessage(string message, LogLevel logLevel, IReadOnlyCollection<KeyValuePair<string, object>>? scopes)
    {
        Hub.Post(new LogMessageRequest(new LogMessage(message, logLevel) { Scopes = scopes }));
    }

    public async Task<ActivityLog> GetLogAsync()
    {
        var response =
            await Hub.AwaitResponse(new GetDataRequest(new EntityReference(nameof(ActivityLog), Address.Id)));
        return (ActivityLog)response.Message.Data!;
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


    protected void LogMessage(LogMessage item)
    {
        // Send LogMessageRequest to handle logging
        Hub.Post(new LogMessageRequest(item));
    }

    public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

    public Activity StartSubActivity(string category)
    {
        var subActivity = new Activity(category, ParentHub);
        Hub.RegisterForDisposal(subActivity);
        SubActivities.Add(subActivity);
        // Monitor sub-activity completion using simple stream subscription with timeout
        var subscription = subActivity.Hub
            .GetWorkspace()
            .GetStream(new EntityReference(nameof(ActivityLog), subActivity.Id))
            .Select(x => x.Value!)
            .OfType<ActivityLog>()
            .Subscribe(
                UpdateSubActivity,
                ex => logger.LogWarning("Error monitoring sub-activity completion for parent {Activity} and sub-activity {SubActivity}: {Exception}", Id, subActivity.Id, ex)
            );
        // Register subscription for cleanup
        Hub.RegisterForDisposal(subscription);
        subActivity.Completion.ContinueWith(t => UpdateSubActivity(t.Result));
        return subActivity;
    }

    private void UpdateSubActivity(ActivityLog subLog)
    {
        Hub.Post(new UpdateActivityLogRequest(activityLog =>
        {
            if (activityLog.SubActivities.TryGetValue(subLog.Id, out var existing) && subLog.Equals(existing))
                return activityLog;
            activityLog = activityLog with
            {
                SubActivities = activityLog.SubActivities.SetItem(subLog.Id, subLog),
                Version = (int)Hub.Version
            };
            // Check if all sub-activities are complete
            if (activityLog.SubActivities.Values.All(subAct => subAct.Status != ActivityStatus.Running))
                Hub.Post(new CompleteActivityRequest(null), options => options.WithTarget(Hub.Address));
            return activityLog;
        }));
    }

    public void Dispose()
    {
        if(!Hub.IsDisposing)    
            Hub.Dispose();
    }
}
