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
        var queryCore = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var path = $"VUser/{virtualUserId}";

        // Existence check via IMeshQueryCore — infrastructure-scoped query (no access
        // control) avoids reaching into IMeshStorage from a non-handler caller. Take
        // the first item and break; if none, the node is missing.
        await foreach (var _ in queryCore.QueryAsync(
            MeshQueryRequest.FromQuery($"path:{path}"),
            hub.JsonSerializerOptions))
        {
            return; // exists
        }

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
                    IsVirtual = true
                }
            };

            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            // Fire-and-forget: await on hub-backed CreateNode deadlocks the hub
            // pump (see AsynchronousCalls.md). Subscribe logs success/failure.
            meshService.CreateNode(userNode).Subscribe(
                _ => logger?.LogDebug("VirtualUser: Created VUser node {Path}", path),
                ex => logger?.LogWarning(ex, "VirtualUser: Failed to create VUser node {Path}", path));
        }
    }
}
