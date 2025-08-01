using System.Text.Json.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data;

public record CompleteActivityRequest(ActivityStatus? Status) : IRequest
{
    [JsonIgnore]public Action<ActivityLog>? CompleteAction { get; init; }
}


public record UpdateActivityLogRequest([property: JsonIgnore] Func<ActivityLog, ActivityLog> Update) : IRequest;
