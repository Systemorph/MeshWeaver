using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public static class ActivityLogExtensions
{
    public static IReadOnlyCollection<LogMessage> Errors(this ActivityLog log) => log.Messages.Where(m => m.LogLevel == LogLevel.Error || m.LogLevel == LogLevel.Critical).Concat(log.SubActivities.SelectMany(c => c.Errors())).ToList();
    public static IReadOnlyCollection<LogMessage> Warnings(this ActivityLog log) => log.Messages.Where(m => m.LogLevel == LogLevel.Warning).Concat(log.SubActivities.SelectMany(c => c.Warnings())).ToList();
    public static IReadOnlyCollection<LogMessage> Infos(this ActivityLog log) => log.Messages.Where(m => m.LogLevel == LogLevel.Information).Concat(log.SubActivities.SelectMany(c => c.Infos())).ToList();


}
