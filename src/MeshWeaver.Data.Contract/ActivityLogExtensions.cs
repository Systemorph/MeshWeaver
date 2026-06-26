using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// Extension helpers for filtering the log messages of an <see cref="ActivityLog"/>
/// (and, recursively, its sub-activities) by severity.
/// </summary>
public static class ActivityLogExtensions
{
    /// <summary>
    /// Returns all error and critical messages from the activity and, recursively, its sub-activities.
    /// </summary>
    /// <param name="log">The activity log to inspect.</param>
    /// <returns>The flattened collection of error/critical messages.</returns>
    public static IReadOnlyCollection<LogMessage> Errors(this ActivityLog log) => log.Messages.Where(m => m.LogLevel == LogLevel.Error || m.LogLevel == LogLevel.Critical).Concat(log.SubActivities.SelectMany(c => c.Errors())).ToList();
    /// <summary>
    /// Returns all warning messages from the activity and, recursively, its sub-activities.
    /// </summary>
    /// <param name="log">The activity log to inspect.</param>
    /// <returns>The flattened collection of warning messages.</returns>
    public static IReadOnlyCollection<LogMessage> Warnings(this ActivityLog log) => log.Messages.Where(m => m.LogLevel == LogLevel.Warning).Concat(log.SubActivities.SelectMany(c => c.Warnings())).ToList();
    /// <summary>
    /// Returns all informational messages from the activity and, recursively, its sub-activities.
    /// </summary>
    /// <param name="log">The activity log to inspect.</param>
    /// <returns>The flattened collection of informational messages.</returns>
    public static IReadOnlyCollection<LogMessage> Infos(this ActivityLog log) => log.Messages.Where(m => m.LogLevel == LogLevel.Information).Concat(log.SubActivities.SelectMany(c => c.Infos())).ToList();


}
