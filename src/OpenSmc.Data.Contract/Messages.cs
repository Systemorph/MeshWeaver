using Json.Patch;
using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record WorkspaceMessage
{
    public object Id { get; init; }
    public object Reference { get; init; }
}

public abstract record DataChangedReqeust(IReadOnlyCollection<object> Elements)
    : IRequest<DataChangeResponse>
{
    public object ChangedBy { get; init; }
};

public record UpdateDataRequest(IReadOnlyCollection<object> Elements) : DataChangedReqeust(Elements)
{
    public UpdateOptions Options { get; init; }
}

public record DeleteDataRequest(IReadOnlyCollection<object> Elements)
    : DataChangedReqeust(Elements);

public record PatchChangeRequest(JsonPatch Change, long Version) : WorkspaceMessage, IRequest<DataChangeResponse>
{
    public object ChangedBy { get; init; }
}

public record DataChangeResponse(long Version, DataChangeStatus Status, ActivityLog Log);

public enum DataChangeStatus
{
    Committed,
    Failed
}

public enum ChangeType
{
    Full,
    Patch,
    Instance
}

public record DataChangedEvent(long Version, RawJson Change, ChangeType ChangeType, object ChangedBy)
    : WorkspaceMessage;

public record SubscribeRequest(WorkspaceReference Reference) : IRequest<DataChangedEvent>;

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
public record UnsubscribeDataRequest(WorkspaceReference Reference);
