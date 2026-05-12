using System.Reactive.Linq;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Infrastructure;

/// <summary>
/// Helper for managing VUser (virtual/anonymous user) nodes.
/// Uses unprotected storage for existence checks and ImpersonateAsHub for creation.
/// </summary>
public static class VUserHelper
{
    /// <summary>
    /// Ensures a VUser node exists for the given virtual user ID.
    /// Uses unprotected storage read for existence check (no security overhead),
    /// and ImpersonateAsHub for creation (VUserAccessRule allows portal namespace).
    /// </summary>
    public static void EnsureVUserNode(PortalApplication portalApp, string virtualUserId, ILogger? logger = null)
    {
        var hub = portalApp.Hub;
        var queryCore = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var path = $"VUser/{virtualUserId}";

        // Existence check via IMeshQueryCore.ObserveQuery — fire-and-forget;
        // subscribe creates the node on the very first emission if no rows.
        queryCore.ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{path}"),
                hub.JsonSerializerOptions)
            .Take(1)
            .Subscribe(
                change =>
                {
                    if (change.Items.Count > 0)
                        return; // exists

                    var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
                    using (accessService.ImpersonateAsHub(hub))
                    {
                        var userNode = new MeshNode(virtualUserId, "VUser")
                        {
                            Name = "Guest",
                            NodeType = "VUser",
                            State = MeshNodeState.Active,
                            Content = new AccessObject { IsVirtual = true }
                        };

                        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
                        meshService.CreateNode(userNode).Subscribe(
                            _ => logger?.LogDebug("VirtualUser: Created VUser node {Path}", path),
                            ex => logger?.LogWarning(ex, "VirtualUser: Failed to create VUser node {Path}", path));
                    }
                },
                ex => logger?.LogWarning(ex, "VirtualUser: existence check failed for {Path}", path));
    }
}
