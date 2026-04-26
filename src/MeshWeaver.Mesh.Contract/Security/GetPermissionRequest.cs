using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Request asking the receiving hub to evaluate effective permissions for the
/// current user on the receiving hub's OWN path. The hub never asks about
/// some other hub's path — callers route the request to the per-node hub at
/// the path they care about, and the handler reads <c>hub.Address</c>.
/// Replies with <see cref="GetPermissionResponse"/>.
///
/// <para><b>Why request/response and not a direct service call?</b>
/// <see cref="ISecurityService"/> is registered <c>Scoped</c> per hub, so it
/// cannot be resolved from the root mesh service provider. Cross-hub callers
/// (tests, other hubs, the portal) post this request and the receiving hub
/// resolves its scoped <c>ISecurityService</c> against its own address.</para>
/// </summary>
public record GetPermissionRequest : IRequest<GetPermissionResponse>;

/// <summary>
/// Response carrying the effective permissions evaluated on a path. Consumers
/// test individual flags via <see cref="System.Enum.HasFlag(System.Enum)"/>.
/// </summary>
/// <param name="Permissions">The OR-combined effective permissions.</param>
public record GetPermissionResponse(Permission Permissions);
