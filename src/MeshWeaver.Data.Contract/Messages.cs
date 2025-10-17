using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public record DataChangeRequest 
    : IRequest<DataChangeResponse>
{
    public string? ChangedBy { get; init; } 
    public IReadOnlyCollection<object> Creations { get; init; } = [];

    public IReadOnlyCollection<object> Updates { get; init; } = [];
    public IReadOnlyCollection<object> Deletions { get; init; } = [];
    public UpdateOptions? Options { get; init; } 
    public string? ClientId { get; init; } 
    public DataChangeRequest WithCreations(params IEnumerable<object> creations)
        => this with { Creations = Creations.Concat(creations).ToArray() };

    public DataChangeRequest WithUpdates(params IEnumerable<object> updates)
        => this with { Updates = Updates.Concat(updates).ToArray() };
    public DataChangeRequest WithDeletions(params IEnumerable<object> deletions)
    => this with { Deletions = Deletions.Concat(deletions).ToArray() };

    public static DataChangeRequest Create(IReadOnlyCollection<object> creations, string changedBy) =>
        new() { Creations = creations, ChangedBy = changedBy };
    public static DataChangeRequest Update(IReadOnlyCollection<object> updates, string? changedBy = null, UpdateOptions? options = null) =>
        new() { Updates = updates, ChangedBy = changedBy!, Options = options! };
    public static DataChangeRequest Delete(IReadOnlyCollection<object> deletes, string changedBy) =>
        new() { Deletions = deletes, ChangedBy = changedBy};

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
/// Request to initialize a synchronization stream during startup.
/// Used to defer messages until initialization is complete.
/// </summary>
public record InitializeStreamRequest(string StreamId) : StreamMessage(StreamId);

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
