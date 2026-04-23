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

    public string? Error { get; init; }
}
