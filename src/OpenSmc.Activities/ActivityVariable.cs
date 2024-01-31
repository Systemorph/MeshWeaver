using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenSmc.ShortGuid;

namespace OpenSmc.Activities
{
    public interface IActivityService : ILogger
    {
        string Start();
        void ChangeStatus(string status);
        bool IsActivityRunning();
        string GetCurrentActivityId();
        bool HasErrors();
        bool HasWarnings();
        void AddSubLog(ActivityLog subLog);
        ActivityLog Finish();
    }

    public class ActivityService : IActivityService
    {
        private readonly ILogger<ActivityService> logger;
        private readonly ConcurrentDictionary<string, ActivityLog> parents = new();
        private ActivityLog currentActivity;

        public ActivityService(ILogger<ActivityService> logger)
        {
            this.logger = logger;
        }

        public string Start()
        {
            var id = Guid.NewGuid().AsString();

            if (currentActivity != null)
            {
                parents[id] = currentActivity;
            }


            //need to know id before, to start scope with known id
            currentActivity = new ActivityLog(id, DateTime.UtcNow, null);
            ChangeStatus(ActivityLogStatus.Started);
            return currentActivity.Id;
        }

        public void ChangeStatus(string status)
        {
            if (currentActivity == null)
                throw new InvalidOperationException("No activity started.");

            currentActivity = currentActivity with { Status = status };
            //var log = new LogMessage($"Activity {currentActivity.Id} started by {sessionVariable.User?.Name}", LogLevel.Information, DateTime.UtcNow, typeof(ActivityService).FullName, new Dictionary<string, object>());
            ////need to know id before, to start scope with known id
            //Log(log);
        }

        public bool IsActivityRunning()
        {
            return currentActivity != null;
        }

        public string GetCurrentActivityId()
        {
            return currentActivity?.Id;
        }

        public bool HasErrors()
        {
            if (currentActivity == null)
                return false;

            return currentActivity.Errors().Any();
        }

        public bool HasWarnings()
        {
            if (currentActivity == null)
                return false;

            return currentActivity.Warnings().Any();
        }

        public void AddSubLog(ActivityLog subLog)
        {
            currentActivity = currentActivity.WithSubLog(subLog);
        }

        public ActivityLog Finish()
        {
            if (currentActivity == null)
                return null;


            if (HasErrors())
                ChangeStatus(ActivityLogStatus.Failed);
            else
                ChangeStatus(ActivityLogStatus.Succeeded);

            var ret = currentActivity;

            if (parents.TryRemove(currentActivity.Id, out var parent))
                currentActivity = parent.WithSubLog(ret);
            else
                currentActivity = null;

            return ret;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            logger.Log(logLevel, eventId, state, exception, formatter);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logger.IsEnabled(logLevel);
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return logger.BeginScope(state);
        }
    }
}
