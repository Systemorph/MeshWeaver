using System.Collections.Immutable;
using System.Text.Json;
using Json.Pointer;
using MeshWeaver.Activities;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public record WorkspaceMessage
{
    public virtual object Owner { get; init; }
    public virtual object Reference { get; init; }
}

public record DataChangeRequest
    : IRequest<DataChangeResponse>
{
    public object ChangedBy { get; init; }
    public ImmutableList<object> Updates { get; init; } = [];
    public ImmutableList<object> Deletions { get; init; } = [];
    public UpdateOptions Options { get; init; }

    public DataChangeRequest WithUpdates(params object[] updates)
    => this with { Updates = Updates.AddRange(updates) };
    public DataChangeRequest WithDeletions(params object[] deletions)
    => this with { Deletions = Deletions.AddRange(deletions) };

    public static DataChangeRequest Update(IReadOnlyCollection<object> updates) =>
        new() { Updates = updates.ToImmutableList() };
    public static DataChangeRequest Delete(IReadOnlyCollection<object> deletes) =>
        new() { Deletions = deletes.ToImmutableList() };
};

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
    Instance,
    NoUpdate
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
