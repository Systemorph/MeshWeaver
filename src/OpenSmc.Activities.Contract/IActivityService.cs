using Microsoft.Extensions.Logging;

namespace OpenSmc.Activities;

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
