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
    public ImmutableDictionary<string, ActivityLog> SubActivities { get; init; } = ImmutableDictionary<string, ActivityLog>.Empty;

    public ActivityLog Fail(string error) =>
        this with
        {
            Messages = Messages.Add(new LogMessage(error, LogLevel.Error)),
            Status = ActivityStatus.Failed,
            End = DateTime.UtcNow,
        };

    public ActivityLog Finish(int version, ActivityStatus? status) =>
        this with
        {
            Status = Messages.Any(x => x.LogLevel == LogLevel.Error)
                ? ActivityStatus.Failed
                : Messages.Any(m => m.LogLevel == LogLevel.Warning) ? ActivityStatus.Warning 
                    : status ?? ActivityStatus.Succeeded,
            End = DateTime.UtcNow,
            Version = version
        };

    public bool HasErrors() => Messages.Any(m => m.LogLevel == LogLevel.Error);
}

