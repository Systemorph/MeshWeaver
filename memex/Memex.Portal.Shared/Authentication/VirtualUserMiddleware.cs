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
/// 2. If no cookie, generates a new GUID, sets a long-lived cookie.
/// 3. Checks if VUser node exists (PingRequest with ImpersonateAsHub), creates if not.
/// 4. Sets AccessContext with IsVirtual = true.
///
/// The VUser check runs once per circuit (page load).
/// Subsequent requests reuse the cached circuit context (same pattern as UserContextMiddleware).
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
            var portalApp = context.RequestServices.GetService<PortalApplication>();
            if (portalApp != null)
            {
                var accessService = portalApp.Hub.ServiceProvider.GetRequiredService<AccessService>();
                var (virtualUserId, isNew) = GetOrCreateVirtualUserId(context);

                // Fast path: if the circuit already has a context for this virtual user, reuse it
                // (avoids mesh call on every request — only needed once per circuit).
                var existing = accessService.Context;
                if (existing is not null && existing.ObjectId == virtualUserId && existing.IsVirtual)
                {
                    accessService.SetContext(existing);
                    await next(context);
                    return;
                }

                // First request in this circuit — ensure VUser node exists.
                // Runs once per page load; subsequent requests reuse CircuitContext above.
                await EnsureVirtualUserNodeAsync(portalApp, virtualUserId);

                var portalIdentity = portalApp.Hub.Address.ToFullString();
                var virtualContext = new AccessContext
                {
                    ObjectId = virtualUserId,
                    Name = "Guest",
                    IsVirtual = true,
                    ImpersonatedBy = portalIdentity
                };
                accessService.SetContext(virtualContext);
                accessService.SetCircuitContext(virtualContext);

                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
                var userAgent = context.Request.Headers.UserAgent.ToString();
                var acceptLanguage = context.Request.Headers.AcceptLanguage.ToString();
                var referer = context.Request.Headers.Referer.ToString();
                logger.LogInformation(
                    "VirtualUser: {VirtualUserId} | New={IsNew} | IP={IP} | ForwardedFor={ForwardedFor} | Language={Language} | Referer={Referer} | UserAgent={UserAgent}",
                    virtualUserId, isNew, ip, forwardedFor, acceptLanguage, referer, userAgent);
            }
        }

        await next(context);
    }

    /// <summary>
    /// Returns the virtual user ID from the cookie, or generates a new one.
    /// The bool indicates whether the ID was newly created.
    /// </summary>
    private static (string id, bool isNew) GetOrCreateVirtualUserId(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var existingId) &&
            !string.IsNullOrEmpty(existingId))
        {
            return (existingId, false);
        }

        var newId = Guid.NewGuid().ToString("N")[..12]; // Short ID for cookie
        context.Response.Cookies.Append(CookieName, newId, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });

        return (newId, true);
    }

    /// <summary>
    /// Checks if the VUser node exists by pinging its hub address.
    /// If the node doesn't exist, creates it via IMeshService.
    /// Uses ImpersonateAsHub scope so the portal hub's address becomes the identity —
    /// VUserAccessRule allows portal namespace.
    /// </summary>
    private async Task EnsureVirtualUserNodeAsync(PortalApplication portalApp, string virtualUserId)
    {
        try
        {
            var hub = portalApp.Hub;
            var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

            using (accessService.ImpersonateAsHub(hub))
            {
                var vUserAddress = new Address("VUser", virtualUserId);

                // Check if VUser node exists by pinging its hub
                var exists = await CheckNodeExistsAsync(hub, vUserAddress);
                if (exists)
                    return;

                // Node doesn't exist — create it
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

                logger.LogDebug("VirtualUser: Created VUser node {Path}", userNode.Path);
            }
        }
        catch (Exception ex)
        {
            // Non-critical — don't block the request on VUser node creation failure
            logger.LogWarning(ex, "VirtualUser: Failed to ensure VUser node for {VirtualUserId}", virtualUserId);
        }
    }

    /// <summary>
    /// Checks if a node exists by sending a PingRequest to its hub address.
    /// Returns true if the hub responds, false if it fails.
    /// </summary>
    private static async Task<bool> CheckNodeExistsAsync(IMessageHub hub, Address nodeAddress)
    {
        try
        {
            await hub.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nodeAddress),
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
