using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Data;

/// <summary>
/// Partial-update request against a workspace reference. The hub handler resolves
/// <c>Reference</c> to its synchronization stream and applies <c>Patch</c> as a
/// JSON merge patch on the current value via <c>stream.Update(...)</c>.
/// Unlike <see cref="PatchDataChangeRequest"/> (which is the stream-sync protocol
/// and requires a pre-existing <c>StreamId</c>), this is a user-facing primitive
/// for one-off writes without a subscription.
/// </summary>
[RequiresPermission(Permission.Update)]
public record PatchDataRequest(WorkspaceReference Reference, RawJson Patch)
    : IRequest<PatchDataResponse>
{
    /// <summary>Identifier of the actor performing the change, if known.</summary>
    public string? ChangedBy { get; init; }

    /// <summary>
    /// The writer's BASE value at exactly the leaves <see cref="Patch"/> changes (same nested shape),
    /// i.e. the old value the diff was computed against. Lets the owner THREE-WAY merge a reordered/stale
    /// cross-hub patch instead of blindly applying last-write-wins: a field unchanged since this base
    /// applies as-is, a string changed on both sides merges by splice, a scalar changed on both sides is
    /// refused (keeping the newer live value). <c>null</c> ⇒ the legacy last-write-wins merge.
    /// See <c>MeshNodePatchMerge</c>.
    /// </summary>
    public RawJson? BaseValues { get; init; }
}

/// <summary>
/// Ack for <see cref="PatchDataRequest"/>. <c>Success</c> is <c>true</c> when the
/// stream applied the patch; <c>Version</c> carries the committed version so the
/// caller can reference it (e.g. in a subsequent optimistic-concurrency write).
/// </summary>
public record PatchDataResponse(bool Success, long Version)
{
    /// <summary>
    /// Inline <see cref="Data.ActivityLog"/> — patch completes synchronously.
    /// Carries merge outcome, validator decisions, stream-commit result.
    /// </summary>
    public ActivityLog? Log { get; init; }

    /// <summary>
    /// Free-text legacy error message. Prefer <see cref="NodeError"/> for
    /// structured failures — consumers can switch on the typed
    /// <see cref="MeshNodeErrorCode"/> instead of pattern-matching strings.
    /// Kept for back-compat with callers that only read <see cref="Error"/>.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Structured failure payload — populated by the owner-side handler when
    /// the operation fails with a recognizable cause (access denied,
    /// deserialization, conflict, etc.). Consumers translate this to a
    /// <see cref="MeshNodeStreamException"/> at the
    /// <c>MeshNodeStreamHandle</c> boundary; the Blazor layout-area pipeline
    /// renders a typed error card per <see cref="MeshNodeErrorCode"/>.
    /// <para>Both <see cref="NodeError"/> and <see cref="Error"/> are
    /// populated on failure — the latter is filled from
    /// <see cref="MeshNodeError.Message"/> so legacy string-only callers
    /// keep working.</para>
    /// </summary>
    public MeshNodeError? NodeError { get; init; }

    /// <summary>
    /// The version the patched ENTITY carries after this patch, as the OWNER committed it — the
    /// version it minted for an applied patch, or the live version when the patch changed nothing.
    /// Set on a successful MeshNode patch; <c>null</c> otherwise, and from an owner on an image that
    /// predates it. Not <see cref="PatchDataResponse.Version"/>, which is the hub's message version.
    /// <para>The writer's acknowledged node carries this version, so it can be named as a
    /// precondition (<c>DeleteNodeRequest.ExpectedVersion</c>) without a re-read (#6856). Before, the
    /// acknowledged node carried the writer's BASE version — one behind the owner — and naming it
    /// was refused as a conflict that never happened.</para>
    /// </summary>
    public long? CommittedVersion { get; init; }
}
