using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Data;

[RequiresPermission(Permission.Update)]
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
        new() { Deletions = deletes, ChangedBy = changedBy };

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
    [property: PreventLogging] RawJson Change,
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

[RequiresPermission(Permission.Read)]
public record SubscribeRequest(string StreamId, WorkspaceReference Reference) : IRequest<DataChangedEvent>
{
    public Address Subscriber { get; init; } = null!;

    /// <summary>
    /// The identity (mesh node) that owns this subscription.
    /// For user-facing streams (layout areas), this is the user ID.
    /// For hub-to-hub streams, this is the hub address.
    /// Used by AccessControlPipeline for permission checks.
    /// </summary>
    public string? Identity { get; init; }
}

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
public record UnsubscribeRequest(string StreamId) : StreamMessage(StreamId);

/// <summary>
/// Request to get data by reference (collection or entity), similar to SubscribeRequest but for one-time data retrieval
/// </summary>
/// <param name="Reference">The workspace reference to retrieve data for</param>
[RequiresPermission(Permission.Read)]
public record GetDataRequest(WorkspaceReference Reference) : IRequest<GetDataResponse>;

/// <summary>
/// Response containing the requested data
/// </summary>
/// <param name="Data">The JSON data retrieved from the workspace reference</param>
/// <param name="Version">The version of the data at the time of retrieval</param>
public record GetDataResponse(object? Data, long Version)
{
    /// <summary>
    /// Error message if the request failed.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// Request to update content via unified reference path.
/// Supports data:, content:, and area: path patterns.
/// </summary>
/// <param name="Path">The unified reference path (e.g., data:pricing/id/Collection/entityId, content:collection/file.txt, area:Overview)</param>
/// <param name="Content">The content to update</param>
[RequiresPermission(Permission.Update)]
public record UpdateUnifiedReferenceRequest(string Path, object Content) : IRequest<UpdateUnifiedReferenceResponse>
{
    public string? ChangedBy { get; init; }
}

/// <summary>
/// Response for unified reference update.
/// </summary>
public record UpdateUnifiedReferenceResponse(long Version)
{
    public string? Error { get; init; }
    public bool Success => Error == null;

    public static UpdateUnifiedReferenceResponse Ok(long version) => new(version);
    public static UpdateUnifiedReferenceResponse Fail(string error) => new(0) { Error = error };
}

/// <summary>
/// Request to delete content via unified reference path.
/// Supports data:, content:, and area: path patterns.
/// </summary>
/// <param name="Path">The unified reference path (e.g., data:pricing/id/Collection/entityId, content:collection/file.txt, area:Overview)</param>
[RequiresPermission(Permission.Delete)]
public record DeleteUnifiedReferenceRequest(string Path) : IRequest<DeleteUnifiedReferenceResponse>
{
    public string? ChangedBy { get; init; }
}

/// <summary>
/// Response for unified reference deletion.
/// </summary>
public record DeleteUnifiedReferenceResponse
{
    public string? Error { get; init; }
    public bool Success => Error == null;

    public static DeleteUnifiedReferenceResponse Ok() => new();
    public static DeleteUnifiedReferenceResponse Fail(string error) => new() { Error = error };
}
