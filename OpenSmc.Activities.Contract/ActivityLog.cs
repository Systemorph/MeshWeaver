using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Activities;

public record ActivityLog([property: Key] string Id,
    //string DisplayName, TODO add this later
    //string Category, TODO add this later or think about how to find activity by its process created
    DateTime Start,
    UserInfo User)
{
    public ImmutableList<LogMessage> Messages { get; init; } = ImmutableList<LogMessage>.Empty;
    public string Status { get; init; } = ActivityLogStatus.Started;
    public DateTime? End { get; init; }
    public string Category { get; init; }

    public ImmutableList<ActivityLog> SubActivities { get; init; } = ImmutableList<ActivityLog>.Empty;

    public ActivityLog WithSubLog(ActivityLog subLog) => this with
    {
        SubActivities = SubActivities.Add(subLog),
    };

}