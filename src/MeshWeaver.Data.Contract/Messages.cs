using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Data;

/// <summary>
/// Request to mutate workspace data by creating, updating and/or deleting instances.
/// </summary>
[RequiresPermission(Permission.Update)]
public record DataChangeRequest
    : IRequest<DataChangeResponse>
{
    /// <summary>Identifier of the actor performing the change, if known.</summary>
    public string? ChangedBy { get; init; }
    /// <summary>The instances to create.</summary>
    public IReadOnlyCollection<object> Creations { get; init; } = [];

    /// <summary>The instances to update.</summary>
    public IReadOnlyCollection<object> Updates { get; init; } = [];
    /// <summary>The instances to delete.</summary>
    public IReadOnlyCollection<object> Deletions { get; init; } = [];
    /// <summary>Options controlling how the change is applied (e.g. snapshot semantics).</summary>
    public UpdateOptions? Options { get; init; }
    /// <summary>Optional client correlation id for the originating change.</summary>
    public string? ClientId { get; init; }

    /// <summary>Returns a copy with the given instances appended to <see cref="Creations"/>.</summary>
    /// <param name="creations">The instances to create.</param>
    /// <returns>The updated request.</returns>
    public DataChangeRequest WithCreations(params IEnumerable<object> creations)
        => this with { Creations = Creations.Concat(creations).ToArray() };

    /// <summary>Returns a copy with the given instances appended to <see cref="Updates"/>.</summary>
    /// <param name="updates">The instances to update.</param>
    /// <returns>The updated request.</returns>
    public DataChangeRequest WithUpdates(params IEnumerable<object> updates)
        => this with { Updates = Updates.Concat(updates).ToArray() };
    /// <summary>Returns a copy with the given instances appended to <see cref="Deletions"/>.</summary>
    /// <param name="deletions">The instances to delete.</param>
    /// <returns>The updated request.</returns>
    public DataChangeRequest WithDeletions(params IEnumerable<object> deletions)
    => this with { Deletions = Deletions.Concat(deletions).ToArray() };

    /// <summary>Creates a request that creates the given instances.</summary>
    /// <param name="creations">The instances to create.</param>
    /// <param name="changedBy">Identifier of the actor performing the change.</param>
    /// <returns>The new request.</returns>
    public static DataChangeRequest Create(IReadOnlyCollection<object> creations, string changedBy) =>
        new() { Creations = creations, ChangedBy = changedBy };
    /// <summary>Creates a request that updates the given instances.</summary>
    /// <param name="updates">The instances to update.</param>
    /// <param name="changedBy">Identifier of the actor performing the change.</param>
    /// <param name="options">Optional update options.</param>
    /// <returns>The new request.</returns>
    public static DataChangeRequest Update(IReadOnlyCollection<object> updates, string? changedBy = null, UpdateOptions? options = null) =>
        new() { Updates = updates, ChangedBy = changedBy!, Options = options! };
    /// <summary>Creates a request that deletes the given instances.</summary>
    /// <param name="deletes">The instances to delete.</param>
    /// <param name="changedBy">Identifier of the actor performing the change.</param>
    /// <returns>The new request.</returns>
    public static DataChangeRequest Delete(IReadOnlyCollection<object> deletes, string changedBy) =>
        new() { Deletions = deletes, ChangedBy = changedBy };

};

/// <summary>
/// Response to a <see cref="DataChangeRequest"/>, reporting the committed version and activity log.
/// </summary>
/// <param name="Version">The workspace version after the change was applied.</param>
/// <param name="Log">The activity log describing the change outcome.</param>
public record DataChangeResponse(long Version, ActivityLog Log)
{
    // 🚨 A WARNING is NOT a failure: the change committed, something just logged a warning
    // (e.g. a benign sub-activity note during apply). Mapping Warning → Failed made every
    // stream write-back that produced a warning OnError ("DataChangeRequest failed … status
    // Warning"), which surfaced as a hard error to readers/tests. Only a genuine Failed (or
    // Cancelled) status is a failure; Succeeded and Warning both commit.
    /// <summary>
    /// The committed/failed status of the change, derived from <see cref="Log"/>. A
    /// <see cref="ActivityStatus.Warning"/> still commits.
    /// </summary>
    public DataChangeStatus Status { get; init; } =
        Log.Status switch
        {
            ActivityStatus.Succeeded or ActivityStatus.Warning => DataChangeStatus.Committed,
            _ => DataChangeStatus.Failed
        };
}

/// <summary>
/// Outcome status of a data change.
/// </summary>
public enum DataChangeStatus
{
    /// <summary>The change was committed (possibly with warnings).</summary>
    Committed,
    /// <summary>The change failed and was not committed.</summary>
    Failed
}

/// <summary>
/// The shape of a change carried by a stream message.
/// </summary>
public enum ChangeType
{
    /// <summary>The change is a full snapshot of the value.</summary>
    Full,
    /// <summary>The change is a JSON patch against the previous value.</summary>
    Patch,
    /// <summary>The change carries a single instance.</summary>
    Instance,
    /// <summary>No update occurred (the value is unchanged).</summary>
    NoUpdate
}

/// <summary>
/// Base type for messages carried over a synchronization stream.
/// </summary>
/// <param name="StreamId">The identifier of the stream the message belongs to.</param>
public abstract record StreamMessage(string StreamId);
/// <summary>
/// Base type for stream messages that carry a versioned JSON change.
/// </summary>
/// <param name="StreamId">The identifier of the stream the message belongs to.</param>
/// <param name="Version">The version the change produces.</param>
/// <param name="Change">The raw JSON payload of the change.</param>
/// <param name="ChangeType">The shape of the change (full, patch, instance, …).</param>
/// <param name="ChangedBy">Identifier of the actor that made the change, if known.</param>
public abstract record JsonChange(
    string StreamId,
    long Version,
    [property: PreventLogging] RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy
) : StreamMessage(StreamId);
/// <summary>
/// Event published when the data behind a stream has changed.
/// </summary>
/// <param name="StreamId">The identifier of the stream the message belongs to.</param>
/// <param name="Version">The version the change produces.</param>
/// <param name="Change">The raw JSON payload of the change.</param>
/// <param name="ChangeType">The shape of the change (full, patch, instance, …).</param>
/// <param name="ChangedBy">Identifier of the actor that made the change, if known.</param>
public record DataChangedEvent(
    string StreamId,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy
) : JsonChange(StreamId, Version, Change, ChangeType, ChangedBy);
/// <summary>
/// Stream-sync request that applies a JSON change to a subscribed stream.
/// </summary>
/// <param name="StreamId">The identifier of the stream to apply the change to.</param>
/// <param name="Version">The version the change produces.</param>
/// <param name="Change">The raw JSON payload of the change.</param>
/// <param name="ChangeType">The shape of the change (full, patch, instance, …).</param>
/// <param name="ChangedBy">Identifier of the actor that made the change, if known.</param>
public record PatchDataChangeRequest(
    string StreamId,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy
) : JsonChange(StreamId, Version, Change, ChangeType, ChangedBy);

/// <summary>
/// Request to subscribe to a stream of changes for the given workspace reference.
/// </summary>
/// <param name="StreamId">The identifier to use for the subscription stream.</param>
/// <param name="Reference">The workspace reference describing the data to subscribe to.</param>
[RequiresPermission(Permission.Read)]
public record SubscribeRequest(string StreamId, WorkspaceReference Reference) : IRequest<SubscribeAck>
{
    /// <summary>The address of the subscriber that will receive change events.</summary>
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
/// Acknowledgement sent by the owner hub after a SubscribeRequest is processed.
/// Closes the hub.Observe(subscribeRequest) pending callback immediately so it
/// does not leak into the quiescing check (0.5 s drain budget at test teardown).
/// DataChangedEvents flow independently via RouteStreamMessage.
/// </summary>
public record SubscribeAck;

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
[SystemMessage]
public record UnsubscribeRequest(string StreamId) : StreamMessage(StreamId);

/// <summary>
/// Server-initiated stream error: routed through <c>RouteStreamMessage</c> to the
/// per-stream sub-hub on the subscriber, where it is converted to <c>OnError</c>
/// on the local <c>SynchronizationStream</c>. Plain <see cref="DeliveryFailure"/>
/// is not a <see cref="StreamMessage"/> and so does not get forwarded into the
/// hosted hub — subscribers stay live without ever observing the upstream error.
/// </summary>
public record StreamErrorEvent(string StreamId, string Message) : StreamMessage(StreamId);

/// <summary>
/// Request to get data by reference (collection or entity), similar to SubscribeRequest but for one-time data retrieval
/// </summary>
/// <param name="Reference">The workspace reference to retrieve data for</param>
[RequiresPermission(Permission.Read)]
public record GetDataRequest(WorkspaceReference Reference) : IRequest<GetDataResponse>
{
    /// <summary>
    /// Optional MIME type to request content conversion.
    /// When set to "text/markdown", binary documents (.docx, .pptx, .xlsx) are converted to markdown.
    /// </summary>
    public string? AcceptMimeType { get; init; }
}

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
    /// <summary>Identifier of the actor performing the change, if known.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>
/// Response for unified reference update.
/// </summary>
public record UpdateUnifiedReferenceResponse(long Version)
{
    /// <summary>Error message if the update failed; null on success.</summary>
    public string? Error { get; init; }
    /// <summary>True if the update succeeded (no error).</summary>
    public bool Success => Error == null;

    /// <summary>Creates a successful response with the committed version.</summary>
    /// <param name="version">The committed version.</param>
    /// <returns>A successful response.</returns>
    public static UpdateUnifiedReferenceResponse Ok(long version) => new(version);
    /// <summary>Creates a failed response with the given error message.</summary>
    /// <param name="error">The error message.</param>
    /// <returns>A failed response.</returns>
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
    /// <summary>Identifier of the actor performing the deletion, if known.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>
/// Response for unified reference deletion.
/// </summary>
public record DeleteUnifiedReferenceResponse
{
    /// <summary>Error message if the deletion failed; null on success.</summary>
    public string? Error { get; init; }
    /// <summary>True if the deletion succeeded (no error).</summary>
    public bool Success => Error == null;

    /// <summary>Creates a successful response.</summary>
    /// <returns>A successful response.</returns>
    public static DeleteUnifiedReferenceResponse Ok() => new();
    /// <summary>Creates a failed response with the given error message.</summary>
    /// <param name="error">The error message.</param>
    /// <returns>A failed response.</returns>
    public static DeleteUnifiedReferenceResponse Fail(string error) => new() { Error = error };
}
