using System.Collections.Concurrent;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

internal class ActivityImpl
{

    internal static MessageHubConfiguration ConfigureActivityHub(MessageHubConfiguration configuration)
    {
        var activity = new ActivityImpl(configuration.Address.Id, configuration.ParentHub!);
        return configuration.AddData(data =>
                data.AddSource(source =>
                    source.WithType<ActivityLog>(type =>
                        type.WithKey(instance => instance.Id)
                            .WithInitialData(_ =>
                                Task.FromResult<IEnumerable<ActivityLog>>([new(ActivityCategory.Unknown){Id = configuration.Address.Id}]))
                    )
                )
            )
            .WithHandler<CompleteActivityRequest>((_, request) => activity.HandleCompleteRequest(request))
            .WithHandler<LogMessageRequest>((_, request) => activity.HandleLogRequest(request))
            .WithHandler<UpdateActivityLogRequest>((_, request) => activity.HandleUpdateActivityLogRequest(request))
            .WithHandler<ChangeActivityCategoryRequest>((_,request) => activity.HandlerChangeActivityCategoryRequest(request))
            .WithInitialization(hub => activity.Initialize(hub));
    }

    private IMessageDelivery HandleUpdateActivityLogRequest(IMessageDelivery<UpdateActivityLogRequest> request)
    {
        activityLog = request.Message.Update.Invoke(activityLog) with {Version = (int)Hub.Version};
        RequestChange();
        return request.Processed();
    }

    private IMessageDelivery HandlerChangeActivityCategoryRequest(IMessageDelivery<ChangeActivityCategoryRequest> request)
    {
        activityLog = activityLog with
        {
            Category = request.Message.Category,
            Version = (int)Hub.Version
        };
        RequestChange();
        return request.Processed();
    }

    internal ActivityImpl(string id, IMessageHub parentHub) 
    {
        Id = id;
        ActivityAddress = new ActivityAddress(Id);
            
        this.Logger = parentHub.ServiceProvider.GetRequiredService<ILogger<ActivityImpl>>();
        activityLog = new(ActivityCategory.Unknown) { Id = id };


    }

    internal void Initialize(IMessageHub hub)
    {
        Hub = hub;
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
                    Logger.LogWarning("Exception during completion of Activity {activityId}:\n{exception}",
                        Hub.Address.Id, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Error in CompleteMyself: {Exception}", ex);
            log = log.Fail(ex.ToString());
            completionSource.TrySetException(ex);
        }
        return log;
    }

    private readonly ConcurrentBag<Action<ActivityLog>> completedActions = new();
    private readonly TaskCompletionSource<ActivityLog> completionSource = new();

    /// <summary>
    /// Task that completes when the activity is finished, returning the final ActivityLog
    /// </summary>
    public Task<ActivityLog> Completion => completionSource.Task;

    public void Complete(Action<ActivityLog>? completedAction = null, ActivityStatus? status = null)
    {

        // Direct call to avoid any threading issues - this should work since ProcessActivityCompletion is designed to be non-blocking
        Hub.Post(new CompleteActivityRequest(status){CompleteAction = completedAction});
    }


    public IMessageDelivery HandleCompleteRequest(IMessageDelivery<CompleteActivityRequest> delivery)
    {
        var request = delivery.Message;
        if(request.CompleteAction != null)
            completedActions.Add(request.CompleteAction);
        activityLog = ProcessActivityCompletion(activityLog, request.Status);
        RequestChange();
        if (activityLog.Status > ActivityStatus.Running) 
            Hub.Dispose();
        return delivery.Processed();
    }

    private void RequestChange()
        => Hub.Post(new DataChangeRequest() { Updates = [activityLog] });
    private ActivityLog ProcessActivityCompletion(ActivityLog log, ActivityStatus? status)
    {
        try
        {
                
            // Check if we should complete immediately (no sub-activities or all sub-activities finished)
            if (log.SubActivities.Count == 0 && log.Status == ActivityStatus.Running)
            {
                return CompleteMyself(log, status);
            }

            // If we have sub-activities, check if all are finished
            if (log.SubActivities.Count > 0 && log.Status == ActivityStatus.Running)
            {
                var allSubActivitiesFinished = log.SubActivities.Values.All(y => y.Status != ActivityStatus.Running);
                if (allSubActivitiesFinished)
                {
                    return CompleteMyself(log, status);
                }
                // Note: Sub-activity completion monitoring is now handled through data change events
                // rather than direct stream subscriptions to avoid hanging
            }
            // Complete immediately if ready
            return CompleteMyself(log, status);

        }
        catch (Exception ex)
        {
            Logger.LogError("Error in ProcessActivityCompletion: {Exception}", ex);
            completionSource.TrySetException(ex);
            log = log.Fail(ex.ToString());
            return log;
        }
    }

    public IMessageDelivery HandleLogRequest(IMessageDelivery<LogMessageRequest> request)
    {
        try
        {
            // Update ActivityLog as data
            activityLog = WithLogs(activityLog, [request.Message.LogMessage]);
            RequestChange();
            return request.Processed();
        }
        catch (Exception ex)
        {
            Logger.LogError("Error in HandleLogRequest: {Exception}", ex);
            return request.Failed(ex.ToString());
        }
    }

    private ActivityLog WithLogs(ActivityLog log, IReadOnlyCollection<LogMessage> logMessages)
    {
        return log with
        {
            Messages = log.Messages.AddRange(logMessages),
            Version = log.Version + 1,
            Status = GetMax(log.Status, logMessages)
        };
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







    protected readonly ILogger Logger;

    public string Id { get; private init; }
    public ActivityAddress ActivityAddress { get; private init; }
        

        
        
    /// <summary>
    /// Gets the current ActivityLog from data storage
    /// </summary>


    private IMessageHub Hub { get; set; } = null!; // Initialized in Initialize method
    private ActivityLog activityLog;


}
