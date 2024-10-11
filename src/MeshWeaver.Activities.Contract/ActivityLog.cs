using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Activities;

public record ActivityLog(string Category)
{
    public DateTime Start { get; init; } = DateTime.UtcNow;

    [property: Key]
    public string Id { get; init; } = Guid.NewGuid().AsString();
    public ImmutableList<LogMessage> Messages { get; init; } = ImmutableList<LogMessage>.Empty;
    public string Status { get; init; } = ActivityLogStatus.Running;
    public DateTime? End { get; init; }
    public UserInfo User { get; init; }
    public ImmutableList<ActivityLog> SubActivities { get; init; } =
        ImmutableList<ActivityLog>.Empty;

    public ActivityLog Fail(string error) =>
        this with
        {
            Messages = Messages.Add(new LogMessage(error, LogLevel.Error)),
            Status = ActivityLogStatus.Failed,
            End = DateTime.UtcNow,
        };

    public ActivityLog Finish() =>
        this with
        {
            Status = Messages.Any(x => x.LogLevel == LogLevel.Error)
                ? ActivityLogStatus.Failed
                : ActivityLogStatus.Succeeded,
            End = DateTime.UtcNow,
        };

    public ActivityLog WithSubLog(ActivityLog subLog) =>
        this with
        {
            SubActivities = SubActivities.Add(subLog),
        };
}

