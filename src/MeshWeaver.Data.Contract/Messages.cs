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
    /// <summary>
    /// The change was applied to the in-memory store (possibly with warnings) and is visible to
    /// every subscriber. NOT a durability guarantee: persistence is dispatched to the
    /// System-identity persistence hub AFTER this status is reported, so a crash in that window
    /// can lose an acknowledged change. Callers that need durable-write semantics must observe
    /// the storage backend, not this status.
    /// </summary>
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
public abstract record StreamMessage(string StreamId) : IDiagnosticKeyed
{
    /// <summary>
    /// The stream id — the thing this message is ABOUT.
    ///
    /// <para>🚨 Load-bearing, not cosmetic. One owner hub holds a SEPARATE sync stream per
    /// subscriber/reference, and every one of them posts to the SAME subscriber address with the
    /// SAME message type. Without this component the hub-ingestion <c>MessageStormBreaker</c>
    /// folds all of them into one rate bucket, so a wide, legitimate change fan-out (a bulk import
    /// driving many streams) is indistinguishable from ONE stream in a resubscribe/repost loop —
    /// and the breaker DROPS the fan-out's frames at ingestion. See <see cref="IDiagnosticKeyed"/>.</para>
    /// </summary>
    string IDiagnosticKeyed.DiagnosticKey => StreamId;
}
/// <summary>
/// Base type for stream messages that carry a versioned JSON change.
/// </summary>
/// <param name="StreamId">The identifier of the stream the message belongs to.</param>
/// <param name="Version">The version the change produces.</param>
/// <param name="Change">The raw JSON payload of the change.</param>
/// <param name="ChangeType">The shape of the change (full, patch, instance, …).</param>
/// <param name="ChangedBy">Identifier of the actor that made the change, if known.</param>
/// <param name="BasedOnVersion">The <see cref="Version"/> of the change the producer SENT
/// immediately before this one on the same stream, or <c>-1</c> when unknown (first frame,
/// or a producer that predates the field). This chains consecutive frames so a receiver can
/// DETECT a lost frame: a <see cref="ChangeType.Patch"/> whose <c>BasedOnVersion</c> does not
/// match the version the receiver last applied means a frame between them never arrived
/// (the transport is at-most-once — an Orleans memory stream drops a frame published before
/// the subscriber attached, and can drop under pressure), and the receiver must request a
/// fresh snapshot instead of silently tracking the owner at a permanent deficit
/// (issue #1081: the compile-error overlay frame vanished mid-burst and the mirror sat on
/// "awaiting first data" forever while later patches kept applying cleanly).</param>
public abstract record JsonChange(
    string StreamId,
    long Version,
    [property: PreventLogging] RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy,
    long BasedOnVersion = -1
) : StreamMessage(StreamId);
/// <summary>
/// Event published when the data behind a stream has changed.
/// </summary>
/// <param name="StreamId">The identifier of the stream the message belongs to.</param>
/// <param name="Version">The version the change produces.</param>
/// <param name="Change">The raw JSON payload of the change.</param>
/// <param name="ChangeType">The shape of the change (full, patch, instance, …).</param>
/// <param name="ChangedBy">Identifier of the actor that made the change, if known.</param>
/// <param name="BasedOnVersion">Version of the previously sent frame on this stream
/// (loss-detection chain — see <see cref="JsonChange"/>), or <c>-1</c> when unknown.</param>
public record DataChangedEvent(
    string StreamId,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy,
    long BasedOnVersion = -1
) : JsonChange(StreamId, Version, Change, ChangeType, ChangedBy, BasedOnVersion);
/// <summary>
/// Stream-sync request that applies a JSON change to a subscribed stream.
/// </summary>
/// <param name="StreamId">The identifier of the stream to apply the change to.</param>
/// <param name="Version">The version the change produces.</param>
/// <param name="Change">The raw JSON payload of the change.</param>
/// <param name="ChangeType">The shape of the change (full, patch, instance, …).</param>
/// <param name="ChangedBy">Identifier of the actor that made the change, if known.</param>
/// <param name="BasedOnVersion">Version of the previously sent frame on this stream
/// (loss-detection chain — see <see cref="JsonChange"/>), or <c>-1</c> when unknown.</param>
public record PatchDataChangeRequest(
    string StreamId,
    long Version,
    RawJson Change,
    ChangeType ChangeType,
    string? ChangedBy,
    long BasedOnVersion = -1
) : JsonChange(StreamId, Version, Change, ChangeType, ChangedBy, BasedOnVersion);

/// <summary>
/// Request to subscribe to a stream of changes for the given workspace reference.
/// </summary>
/// <param name="StreamId">The identifier to use for the subscription stream.</param>
/// <param name="Reference">The workspace reference describing the data to subscribe to.</param>
[RequiresPermission(Permission.Read)]
public record SubscribeRequest(string StreamId, WorkspaceReference Reference)
    : IRequest<SubscribeAck>, IDiagnosticKeyed
{
    /// <summary>
    /// The stream id — so the pending-callback diagnostic can tell N unanswered subscribes for N
    /// DIFFERENT streams (a fan-out) from one stream re-asking (a resubscribe loop). See
    /// <see cref="IDiagnosticKeyed"/>; the 167-pending pile on memex-cloud 2026-08-12 was
    /// indistinguishable between the two.
    /// </summary>
    string IDiagnosticKeyed.DiagnosticKey => StreamId;

    /// <summary>The address of the subscriber that will receive change events.</summary>
    public Address Subscriber { get; init; } = null!;

    /// <summary>
    /// The identity (mesh node) that owns this subscription.
    /// For user-facing streams (layout areas), this is the user ID.
    /// For hub-to-hub streams, this is the hub address.
    /// Used by AccessControlPipeline for permission checks.
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// 🚨 THE ONE NEGOTIATED WIRE CAPABILITY of the owner→subscriber fan-out: this subscriber can
    /// apply a <c>splice</c> operation — a changed string leaf carried as
    /// <c>{ "$sd": [start, removed, "inserted"], "$sdb": [baseLength, "fingerprint"] }</c> instead
    /// of the whole new string. Default <c>false</c>, and that default is load-bearing.
    ///
    /// <para><b>Why this is negotiated rather than simply emitted.</b> The write direction
    /// (<c>PatchDataRequest</c>, #1414) could put its splice marker straight on the wire because the
    /// only consumer is a C# owner, which fails LOUDLY on a shape it does not understand — the
    /// merged node stops deserialising, the write is NACKed and never commits. The fan-out has no
    /// such property. Its consumers include four hand-rolled appliers this repo's CI does not build
    /// — <c>clients/grpc-web</c>, two in <c>clients/react</c>, and <c>clients/python</c> — and every
    /// one of them fails SILENTLY: the JS appliers treat an unknown <c>op</c> as a no-op (the text
    /// would simply stop updating mid-stream), and the Python one applies ANY unrecognised op as a
    /// replace, writing <c>None</c> into the field. A browser also holds whatever bundle it loaded,
    /// so "upgrade both ends together" is not available here the way it is between two pods.</para>
    ///
    /// <para>So the rule is the same one #1414 applied, reached from the other side: <b>never let a
    /// consumer half-apply a frame</b>. A subscriber that does not say it understands splices gets
    /// byte-identical bytes to what it gets today — no migration, no flag day, no shape it can
    /// misread. The marker still rides INSIDE the patch, so a subscriber that claims the capability
    /// and then cannot honour it still fails loudly (C#: <c>Unknown patch operation</c>).</para>
    ///
    /// <para>An unknown property is skipped by the messaging serializer
    /// (<c>UnmappedMemberHandling.Skip</c>), so this is safe in both rolling-deploy directions: a
    /// new subscriber against an old owner is simply not spliced to, and an old subscriber against a
    /// new owner never sets it.</para>
    /// </summary>
    public bool AcceptsStringSplice { get; init; }
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

    /// <summary>
    /// Why <see cref="Data"/> is null, when the owner knows a reason the caller must be able to
    /// tell apart from ordinary absence. Defaults to <see cref="DataAbsenceReason.Unspecified"/>,
    /// which serialises away (<c>DefaultIgnoreCondition=WhenWritingDefault</c>) — only a
    /// non-default reason travels, so nothing changes for the overwhelming majority of reads.
    /// </summary>
    public DataAbsenceReason Absence { get; init; }
}

/// <summary>
/// Why a <see cref="GetDataResponse"/> carries no data. Exists because a null
/// <see cref="GetDataResponse.Data"/> is NOT one fact: "there is nothing here" and "there is
/// something here and it is being deleted" are different answers, and a caller that treats the
/// second as the first re-creates what a user just deleted (Systemorph/MeshWeaver#1471).
/// </summary>
public enum DataAbsenceReason
{
    /// <summary>
    /// No reason recorded — the ordinary case: data is present, or absent because there is
    /// nothing at the reference.
    /// </summary>
    Unspecified,

    /// <summary>
    /// The entity exists but its DELETE is in flight, so the owner answered with its tombstone
    /// rather than the stale content (<c>MeshDataSource.AddReadValidatorPipeline</c>). TRANSIENT
    /// and directional — the next authoritative answer is "gone", never "here again".
    /// </summary>
    DeleteInProgress,
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
