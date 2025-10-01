using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data;

public record ActivityLog(string Category)
{
    public DateTime Start { get; init; } = DateTime.UtcNow;
    public int Version { get; init; }

    [property: Key]
    public string Id { get; init; } = Guid.NewGuid().AsString();
    public ImmutableList<LogMessage> Messages { get; init; } = ImmutableList<LogMessage>.Empty;
    public ActivityStatus Status { get; init; }
    public DateTime? End { get; init; }
    public UserInfo? User { get; init; }
    public ImmutableList<ActivityLog> SubActivities { get; init; } = [];

    public ActivityLog Fail(string error) =>
        this with
        {
            Messages = Messages.Add(new LogMessage(error, LogLevel.Error)),
            Status = ActivityStatus.Failed,
            End = DateTime.UtcNow,
        };

    public ActivityLog Finish(int version, ActivityStatus? _) =>
        this with
        {
            Status = GetFinalStatus(),
            End = DateTime.UtcNow,
            Version = version
        };

    private ActivityStatus GetFinalStatus()
    {
        var subActivityStatus = SubActivities
            .Select(s => s.Status)
            .DefaultIfEmpty(ActivityStatus.Succeeded)
            .Max();

        var maxLevel = Messages.Select(m => m.LogLevel).DefaultIfEmpty(LogLevel.Information).Max();
        var mapToStatus = maxLevel switch
        {
            LogLevel.Critical or LogLevel.Error => ActivityStatus.Failed,
            LogLevel.Warning => ActivityStatus.Warning, 
            _ => ActivityStatus.Succeeded
        };

        return subActivityStatus > mapToStatus
            ? subActivityStatus
            : mapToStatus;
    }

    public bool HasErrors() => Messages.Any(m => m.LogLevel == LogLevel.Error);
}

