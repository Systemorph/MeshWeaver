using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data;

public record CompleteActivityRequest(ActivityStatus? Status) : IRequest
{
    public Action<ActivityLog>? CompleteAction { get; init; }
}

public record LogRequest(LogMessage LogMessage) : IRequest;

public record StartSubActivityRequest(string Category) : IRequest
{
    public string SubActivityId { get; init; } = Guid.NewGuid().AsString();
}
