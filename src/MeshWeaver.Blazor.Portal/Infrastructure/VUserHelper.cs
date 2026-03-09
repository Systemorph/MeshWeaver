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
    public static async Task EnsureVUserNodeAsync(PortalApplication portalApp, string virtualUserId, ILogger? logger = null)
    {
        var hub = portalApp.Hub;
        var persistence = hub.ServiceProvider.GetRequiredService<IMeshStorage>();
        var path = $"VUser/{virtualUserId}";

        if (await persistence.ExistsAsync(path))
            return;

        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsHub(hub))
        {
            var userNode = new MeshNode(virtualUserId, "VUser")
            {
                Name = "Guest",
                NodeType = "VUser",
                State = MeshNodeState.Active,
                Content = new AccessObject
                {
                    Id = virtualUserId,
                    Name = "Guest",
                    IsVirtual = true
                }
            };

            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            await meshService.CreateNodeAsync(userNode, CancellationToken.None);
            logger?.LogDebug("VirtualUser: Created VUser node {Path}", path);
        }
    }
}
