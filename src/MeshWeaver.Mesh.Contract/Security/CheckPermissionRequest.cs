using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Posted to the per-node hub of a resource to ask "is this user (with these
/// group memberships) granted this permission here?". The hub answers from
/// its locally-synced <c>EffectiveAssignments</c> + <c>LocalPolicies</c>
/// collections — see <c>Doc/Architecture/AccessControl.md</c>.
/// <para>
/// <paramref name="GroupPaths"/> is populated by the caller via a separate
/// <see cref="GetGroupMembershipsRequest"/> to the user's per-user hub. The
/// resource hub does not have a global view of group memberships and
/// trusts the caller (typically the framework's access pipeline) to
/// resolve them.
/// </para>
/// </summary>
public record CheckPermissionRequest(
    string UserId,
    ImmutableList<string> GroupPaths,
    Permission Permission)
    : IRequest<CheckPermissionResponse>;

/// <summary>
/// Response to <see cref="CheckPermissionRequest"/>.
/// </summary>
/// <param name="IsGranted">True iff <c>Permission</c> is granted.</param>
/// <param name="EffectivePermissions">
/// All permission flags this user effectively has on the resource —
/// useful for UI helpers that want to show "Can I edit?" without a
/// follow-up call.
/// </param>
/// <param name="DenialReason">
/// Human-readable reason when <see cref="IsGranted"/> is false. Null on
/// success.
/// </param>
public record CheckPermissionResponse(
    bool IsGranted,
    Permission EffectivePermissions,
    string? DenialReason);
