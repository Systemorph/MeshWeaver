using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Middleware that assigns a virtual user identity to unauthenticated visitors.
/// Runs after authentication. If the user is NOT authenticated:
/// 1. Checks for a "meshweaver_virtual_user" cookie.
/// 2. If no cookie, generates a new GUID and sets a long-lived cookie.
/// 3. Creates a virtual User node if it doesn't exist.
/// 4. Sets AccessContext with IsVirtual = true.
/// </summary>
public class VirtualUserMiddleware(RequestDelegate next, ILogger<VirtualUserMiddleware> logger)
{
    private const string CookieName = "meshweaver_virtual_user";

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip virtual user assignment for MCP requests — they should get 401, not a virtual identity
        if (context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            var virtualUserId = GetOrCreateVirtualUserId(context);

            // Set access context for virtual user
            var portalApp = context.RequestServices.GetService<PortalApplication>();
            if (portalApp != null)
            {
                var accessService = portalApp.Hub.ServiceProvider.GetRequiredService<AccessService>();
                var virtualContext = new AccessContext
                {
                    ObjectId = virtualUserId,
                    Name = "Guest",
                    IsVirtual = true
                };
                accessService.SetContext(virtualContext);
                accessService.SetCircuitContext(virtualContext);

                logger.LogDebug("VirtualUser: assigned virtual user {VirtualUserId}", virtualUserId);

                // Ensure the virtual user node exists
                await EnsureVirtualUserNodeAsync(portalApp, virtualUserId);
            }
        }

        await next(context);
    }

    private static string GetOrCreateVirtualUserId(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var existingId) &&
            !string.IsNullOrEmpty(existingId))
        {
            return existingId;
        }

        var newId = Guid.NewGuid().ToString("N")[..12]; // Short ID for cookie
        context.Response.Cookies.Append(CookieName, newId, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });

        return newId;
    }

    private static async Task EnsureVirtualUserNodeAsync(PortalApplication portalApp, string virtualUserId)
    {
        var nodeFactory = portalApp.Hub.ServiceProvider.GetRequiredService<IMeshNodeFactory>();

        try
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
            await nodeFactory.CreateNodeAsync(userNode, "VirtualUserMiddleware");
        }
        catch (InvalidOperationException)
        {
            // Node already exists — ignore
        }
    }
}
