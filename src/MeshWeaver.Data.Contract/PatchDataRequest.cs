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
    public string? ChangedBy { get; init; }
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
}
