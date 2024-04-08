using Json.Patch;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record UpdateDataRequest(IReadOnlyCollection<object> Elements) : DataChangeRequestWithElements(Elements)
{
    public UpdateOptions Options { get; init; }
}

public record DeleteDataRequest(IReadOnlyCollection<object> Elements) : DataChangeRequestWithElements(Elements);


public abstract record DataChangeRequestWithElements(IReadOnlyCollection<object> Elements) : DataChangeRequest;

public abstract record DataChangeRequest : IRequest<DataChangeResponse>;

public record DataChangeResponse(long Version, DataChangeStatus Status);

public enum DataChangeStatus{Committed, Failed}

public record CreateRequest<TObject>(TObject Element) : IRequest<DataChangedEvent> { public object Options { get; init; } };


public record SubscribeRequest(WorkspaceReference Reference) : IRequest<DataChangedEvent>;

public enum ChangeType{ Full, Patch, Instance }
public record DataChangedEvent(object Address, WorkspaceReference Reference, long Version, object Change, ChangeType ChangeType, object ChangedBy);


/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
public record UnsubscribeDataRequest(WorkspaceReference Reference);

public record PatchChangeRequest(object Address, WorkspaceReference Reference, JsonPatch Change) : DataChangeRequest;
