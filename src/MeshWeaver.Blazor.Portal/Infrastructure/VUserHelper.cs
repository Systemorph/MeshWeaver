using System.Reactive.Linq;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Infrastructure;

/// <summary>
/// Helper for managing VUser (virtual/anonymous user) nodes.
/// </summary>
public static class VUserHelper
{
    /// <summary>
    /// Ensures a VUser node exists for the given virtual user ID. Posts a
    /// <see cref="CreateNodeRequest"/> with skip-on-exists semantics — the
    /// handler rejects with <see cref="NodeCreationRejectionReason.NodeAlreadyExists"/>
    /// when the node is already there, which we treat as success. No
    /// existence query, no race.
    /// </summary>
    public static void EnsureVUserNode(PortalApplication portalApp, string virtualUserId, ILogger? logger = null)
    {
        var hub = portalApp.Hub;
        var path = $"VUser/{virtualUserId}";
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        var userNode = new MeshNode(virtualUserId, "VUser")
        {
            Name = "Guest",
            NodeType = "VUser",
            State = MeshNodeState.Active,
            Content = new AccessObject { IsVirtual = true }
        };

        // 🚨 CreateNodeRequest must target the MESH hub — that's where
        // WithNodeOperationHandlers registers the handler. PortalApp.Hub is a
        // hosted hub at `portal/{userId}` (or `portal/anonymous`) which has no
        // CreateNodeRequest handler, so a bare Observe without a target sends
        // the request to portal/anonymous and prod surfaces
        // "No handler found for message type CreateNodeRequest in portal/anonymous"
        // — the page-open crash a real user just hit on the sub-thread URL.
        var meshHub = hub.GetMeshHub();

        // Provisioning a guest VUser node is an infrastructure write, so it runs as
        // the well-known system identity — NOT ImpersonateAsHub(hub). For an
        // anonymous session `hub` is `portal/anonymous`, a hub-shaped principal;
        // RestoreUserContextOnEmission's leak-guard rejects hub-shaped principals
        // ("SetContext: hub-shaped principal … must never happen") and logged an
        // Error on every anonymous request. `system-security` is a real principal
        // with Permission.All, so it passes the guard. Subscribe stays INSIDE the
        // scope so the emission-side context is system, not the leaked hub identity.
        using (accessService.ImpersonateAsSystem())
        {
            hub.Observe<CreateNodeResponse>(
                    new CreateNodeRequest(userNode),
                    o => o.WithTarget(meshHub.Address))
                .FirstAsync()
                .Subscribe(
                    delivery =>
                    {
                        var resp = delivery.Message;
                        if (resp.Success)
                            logger?.LogDebug("VirtualUser: Created VUser node {Path}", path);
                        else if (resp.RejectionReason == NodeCreationRejectionReason.NodeAlreadyExists)
                            logger?.LogDebug("VirtualUser: VUser node {Path} already exists", path);
                        else
                            logger?.LogWarning("VirtualUser: Failed to create VUser node {Path}: {Error}", path, resp.Error);
                    },
                    ex => logger?.LogWarning(ex, "VirtualUser: Failed to ensure VUser node {Path}", path));
        }
    }
}
