﻿using MeshWeaver.Activities;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public record WorkspaceMessage
{
    public virtual object Owner { get; init; }
    public virtual object Reference { get; init; }
}

public abstract record DataChangedRequest(IReadOnlyCollection<object> Elements)
    : IRequest<DataChangeResponse>
{
    public object ChangedBy { get; init; }
};

public record UpdateDataRequest(IReadOnlyCollection<object> Elements) : DataChangedRequest(Elements)
{
    public UpdateOptions Options { get; init; }
}

public record DeleteDataRequest(IReadOnlyCollection<object> Elements)
    : DataChangedRequest(Elements);

public record DataChangeResponse(long Version, ActivityLog Log)
{
    public DataChangeStatus Status { get; init; } =
        Log.Status switch
        {
            ActivityStatus.Succeeded => DataChangeStatus.Committed,
            _ => DataChangeStatus.Failed
        };
}

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
    object Owner,
    object Reference,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    object ChangedBy
);

public record SubscribeRequest(WorkspaceReference Reference) : IRequest<DataChangedEvent>;

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
public record UnsubscribeDataRequest(WorkspaceReference Reference);
