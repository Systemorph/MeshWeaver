using System.Collections.Concurrent;
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
        Hub.Post(new LogRequest(new LogMessage(message, logLevel) { Scopes = scopes }));
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
        // Send LogRequest to handle logging
        Hub.Post(new LogRequest(item));
    }

    public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

    public Activity StartSubActivity(string category)
    {
        var subActivity = new Activity(category, ParentHub);
        Hub.Post(new StartSubActivityRequest(category) {  SubActivityId = subActivity.Id });
        SubActivities.Add(subActivity);
        return subActivity;
    }

    public void Dispose()
    {
        Hub.Dispose();
    }
}
