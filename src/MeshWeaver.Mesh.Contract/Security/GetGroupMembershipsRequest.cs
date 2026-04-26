using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Posted to a per-user hub (<c>User/{userId}</c>) to fetch the user's
/// current group memberships. The hub answers from its locally-synced
/// <c>GroupMemberships</c> collection — populated reactively from the
/// mesh-wide <c>GroupMembership</c> nodes via
/// <c>WithMeshQuery&lt;GroupMembership&gt;</c>. See
/// <c>Doc/Architecture/AccessControl.md</c>.
/// <para>
/// Used by the access-control pipeline as the first step of a
/// permission check: ask the user hub for groups, then ask the resource
/// hub <see cref="CheckPermissionRequest"/> with those groups attached.
/// </para>
/// </summary>
public record GetGroupMembershipsRequest()
    : IRequest<GetGroupMembershipsResponse>;

/// <summary>
/// Response to <see cref="GetGroupMembershipsRequest"/>.
/// </summary>
/// <param name="GroupPaths">
/// Paths of every group the user currently belongs to. Live snapshot from
/// the user-hub workspace — populated by the change notifier within
/// milliseconds of any membership write.
/// </param>
public record GetGroupMembershipsResponse(
    ImmutableList<string> GroupPaths);
