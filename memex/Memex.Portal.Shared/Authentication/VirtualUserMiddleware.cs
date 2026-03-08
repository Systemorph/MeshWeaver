using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
/// 3. Creates a virtual User node if it doesn't exist (via CreateNodeRequest with ImpersonateAsHub).
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
                await EnsureVirtualUserNodeAsync(portalApp, virtualUserId);

                // Set virtual user context for the rest of the request
                var accessService = portalApp.Hub.ServiceProvider.GetRequiredService<AccessService>();
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

                logger.LogDebug("VirtualUser: assigned virtual user {VirtualUserId}", virtualUserId);
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

    /// <summary>
    /// Creates the VUser node by posting CreateNodeRequest through the portal hub
    /// with ImpersonateAsHub(), so the hub's address becomes the identity.
    /// The routing service handles the request.
    /// </summary>
    private static async Task EnsureVirtualUserNodeAsync(PortalApplication portalApp, string virtualUserId)
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

        try
        {
            var response = await portalApp.Hub.AwaitResponse(
                new CreateNodeRequest(userNode),
                o => o.ImpersonateAsHub(),
                CancellationToken.None);

            if (!response.Message.Success &&
                response.Message.RejectionReason != NodeCreationRejectionReason.NodeAlreadyExists)
            {
                throw new InvalidOperationException(
                    $"Failed to create VUser node: {response.Message.Error}");
            }
        }
        catch (InvalidOperationException)
        {
            // Node already exists (cookie reuse) — ignore
        }
    }
}
