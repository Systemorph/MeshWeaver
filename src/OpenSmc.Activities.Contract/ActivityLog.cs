using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.ShortGuid;

namespace OpenSmc.Activities;

public record ActivityLog(
    //string DisplayName, TODO add this later
    //string Category, TODO add this later or think about how to find activity by its process created
    DateTime Start,
    UserInfo User)
{

    [property: Key] public string Id { get; init; } = Guid.NewGuid().AsString();
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