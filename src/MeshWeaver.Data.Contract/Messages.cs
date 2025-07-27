using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public record DataChangeRequest 
    : IRequest<DataChangeResponse>
{
    public string? ChangedBy { get; init; } 
    public ImmutableList<object> Creations { get; init; } = [];

    public ImmutableList<object> Updates { get; init; } = [];
    public ImmutableList<object> Deletions { get; init; } = [];
    public UpdateOptions? Options { get; init; } 
    public string? ClientId { get; init; } 
    public DataChangeRequest WithCreations(params object[] creations)
        => this with { Creations = Creations.AddRange(creations) };

    public DataChangeRequest WithUpdates(params object[] updates)
        => this with { Updates = Updates.AddRange(updates) };
    public DataChangeRequest WithUpdates(IEnumerable<object> updates)
        => this with { Updates = Updates.AddRange(updates) };
    public DataChangeRequest WithDeletions(params object[] deletions)
    => this with { Deletions = Deletions.AddRange(deletions) };

    public static DataChangeRequest Create(IReadOnlyCollection<object> creations, string changedBy) =>
        new() { Creations = creations.ToImmutableList(), ChangedBy = changedBy };
    public static DataChangeRequest Update(IReadOnlyCollection<object> updates, string? changedBy = null, UpdateOptions? options = null) =>
        new() { Updates = updates.ToImmutableList(), ChangedBy = changedBy!, Options = options! };
    public static DataChangeRequest Delete(IReadOnlyCollection<object> deletes, string changedBy) =>
        new() { Deletions = deletes.ToImmutableList(), ChangedBy = changedBy};

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

public abstract record StreamMessage(string StreamId);
public abstract record JsonChange(
    string StreamId,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy
) : StreamMessage(StreamId);
public record DataChangedEvent(
    string StreamId,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy
) : JsonChange(StreamId, Version, Change, ChangeType, ChangedBy);
public record PatchDataChangeRequest(
    string StreamId,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy
) : JsonChange(StreamId, Version, Change, ChangeType, ChangedBy);

public record SubscribeRequest(string StreamId, WorkspaceReference Reference) : IRequest<DataChangedEvent>
{
    public Address Subscriber { get; init; } = null!;
}

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
public record UnsubscribeRequest(string StreamId) : StreamMessage(StreamId);

/// <summary>
/// Request to get data by reference (collection or entity), similar to SubscribeRequest but for one-time data retrieval
/// </summary>
/// <param name="Reference">The workspace reference to retrieve data for</param>
public record GetDataRequest(WorkspaceReference Reference) : IRequest<GetDataResponse>;

/// <summary>
/// Response containing the requested data
/// </summary>
/// <param name="Data">The JSON data retrieved from the workspace reference</param>
/// <param name="Version">The version of the data at the time of retrieval</param>
public record GetDataResponse(object? Data, long Version)
{
    public string? Error { get; init; }
}
