using Microsoft.Extensions.Logging;

namespace MeshWeaver.Activities;

public interface IActivityService : ILogger
{
    string Start(string category);
    void ChangeStatus(string status);
    bool IsActivityRunning();
    string GetCurrentActivityId();
    bool HasErrors();
    bool HasWarnings();
    void AddSubLog(ActivityLog subLog);
    ActivityLog Finish();
    ActivityLog GetCurrentActivityLog();
}
