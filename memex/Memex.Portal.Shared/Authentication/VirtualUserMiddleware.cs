using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Portal.Infrastructure;
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

    private static readonly string[] ExcludedPrefixes =
        ["/_framework", "/_content", "/_blazor", "/static/", "/favicon.ico", "/mcp", "/bootstrap", "/healthz"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
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

                // 🚨 Skip the entire VUser flow when a real-user identity is
                // already on AccessService — `UserContextMiddleware` runs
                // BEFORE us in the pipeline (see MemexConfiguration.cs) and
                // resolves OAuth / Bearer / mesh-User-by-email into
                // AccessService.Context. Real users sometimes have
                // `context.User.Identity.IsAuthenticated == false` (e.g.,
                // the ASP.NET cookie expired but a Bearer token in the
                // request still resolves a valid user via the cache); without
                // this guard we'd wastefully provision a guest VUser node
                // for them AND post the CreateNodeRequest into the portal
                // hub, which has no handler — the crash the user just hit
                // on the sub-thread URL.
                var preExisting = accessService.Context ?? accessService.CircuitContext;
                if (preExisting is not null && !preExisting.IsVirtual
                    && !string.IsNullOrEmpty(preExisting.ObjectId))
                {
                    await next(context);
                    return;
                }

                var (virtualUserId, isNew) = GetOrCreateVirtualUserId(context);

                // Fast path: if the circuit already has a context for this virtual user, reuse it
                // (avoids mesh call on every request — only needed once per circuit).
                if (preExisting is not null && preExisting.ObjectId == virtualUserId && preExisting.IsVirtual)
                {
                    accessService.SetContext(preExisting);
                    await next(context);
                    return;
                }

                // 🚨 Mint the VUser NODE only on cookie ROUND-TRIP (isNew == false):
                // the node (a mesh write + a per-node hub graph) is created the
                // first time the cookie COMES BACK, proving a cookie-keeping
                // browser session. First-contact requests get the cookie + an
                // in-memory guest context only. Without this gate, every
                // cookie-less client minted a fresh node PER REQUEST — kube-probe
                // alone created ~15 VUsers/minute (and any crawler does the same),
                // leaking 10,000+ hubs until the portal wedged at 100% CPU
                // (2026-06-12 atioz outage). A real visitor's node exists by
                // their second request — before the Blazor circuit needs it.
                //
                // 100% reactive, NO await: EnsureVUserNode composes
                // hub.Observe(CreateNodeRequest).Subscribe(...) internally — the
                // request thread never waits on mesh work (AsynchronousCalls.md;
                // an await here parks the request on the hub pump = deadlock).
                if (!isNew)
                    EnsureVirtualUserNode(portalApp, virtualUserId);

                var portalIdentity = portalApp.Hub.Address.ToFullString();
                var virtualContext = new AccessContext
                {
                    ObjectId = virtualUserId,
                    Name = "Guest",
                    IsVirtual = true,
                    ImpersonatedBy = portalIdentity
                };
                // Set per-request context only. CircuitAccessHandler handles
                // per-circuit persistence via CreateInboundActivityHandler.
                accessService.SetContext(virtualContext);

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

    private void EnsureVirtualUserNode(PortalApplication portalApp, string virtualUserId)
    {
        try
        {
            VUserHelper.EnsureVUserNode(portalApp, virtualUserId, logger);
        }
        catch (Exception ex)
        {
            // Non-critical — don't block the request on VUser node creation failure
            logger.LogWarning(ex, "VirtualUser: Failed to ensure VUser node for {VirtualUserId}", virtualUserId);
        }
    }
}
