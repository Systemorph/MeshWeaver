using Json.Patch;
using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public interface IWorkspaceMessage
{
    object Address { get; }
    object Reference { get; }
}

public abstract record DataChangeRequest() : IRequest<DataChangeResponse>
{
    public object ChangedBy { get; init; }
}

public abstract record DataChangeRequestWithElements(IReadOnlyCollection<object> Elements)
    : DataChangeRequest;

public record UpdateDataRequest(IReadOnlyCollection<object> Elements)
    : DataChangeRequestWithElements(Elements)
{
    public UpdateOptions Options { get; init; }
}

public record DeleteDataRequest(IReadOnlyCollection<object> Elements)
    : DataChangeRequestWithElements(Elements);

public record PatchChangeRequest(object Address, object Reference, JsonPatch Change, long Version)
    : DataChangeRequest,
        IWorkspaceMessage { }

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

public record DataChangedEvent(
    object Address,
    object Reference,
    long Version,
    object Change,
    ChangeType ChangeType,
    object ChangedBy
) : IWorkspaceMessage;

public record SubscribeRequest(WorkspaceReference Reference) : IRequest<DataChangedEvent>;

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
public record UnsubscribeDataRequest(WorkspaceReference Reference);
