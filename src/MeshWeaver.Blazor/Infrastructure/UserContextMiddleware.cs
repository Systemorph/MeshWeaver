using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Infrastructure;

public class UserContextMiddleware(RequestDelegate next, ILogger<UserContextMiddleware> logger)
{
    // Blazor framework files, static assets, and favicon — no user context needed.
    private static readonly string[] ExcludedPrefixes =
        ["/_framework", "/_content", "/_blazor", "/static/", "/favicon.ico"];

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip user resolution for static assets and Blazor framework resources.
        // These requests never need an AccessContext and resolving it adds unnecessary
        // overhead (hub lookup, mesh query) on every JS/CSS/SignalR resource download.
        var path = context.Request.Path.Value ?? "";
        if (ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var hub = context.RequestServices.GetRequiredService<PortalApplication>().Hub;
        var userService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // Try OAuth first (browser sessions), then Bearer token (MCP / API clients)
        var userContext = ExtractUserContext(context.User)
                          ?? await ExtractFromBearerTokenAsync(context.Request, hub);

        if (userContext is not null)
        {
            // If this request already has a resolved context (same email), reuse it.
            var existing = userService.Context;
            if (existing is not null && existing.Email == userContext.Email)
            {
                userService.SetContext(existing);
                await next(context);
                return;
            }

            // Resolve ObjectId from mesh User node (email -> username).
            // Without this, ObjectId stays as the email (from claims), causing permission
            // lookups to fail since AccessAssignment nodes use the username, not the email.
            if (!string.IsNullOrEmpty(userContext.Email))
            {
                var meshUser = await TryLoadMeshUserAsync(userContext.Email, hub);
                if (meshUser is not null)
                {
                    userContext = userContext with
                    {
                        ObjectId = meshUser.Id,
                        Name = meshUser.Name ?? meshUser.Id
                    };
                }
            }

            // Set per-request AsyncLocal only. CircuitAccessHandler handles
            // per-circuit persistence via CreateInboundActivityHandler.
            userService.SetContext(userContext);
        }
        else
        {
            userService.SetContext(null);
        }

        await next(context);
    }

    /// <summary>
    /// Validates a Bearer token by sending a ValidateTokenRequest to the token's hub address.
    /// The ApiToken node type handler validates hash/expiry/revocation and returns user info.
    /// This gives the token holder the exact same access rights as the user who created the token.
    /// </summary>
    private async Task<AccessContext?> ExtractFromBearerTokenAsync(HttpRequest request, IMessageHub hub)
    {
        var authHeader = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var rawToken = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(ValidateTokenRequest.TokenPrefix))
            return null;

        var response = await ValidateTokenViaHubAsync(rawToken, hub);
        if (response is not { Success: true })
            return null;

        return new AccessContext
        {
            // ObjectId must be the mesh User.Id (e.g. "rbuergi"), not the display name.
            // RLS compares context.Node.Path against `User/{ObjectId}` for self-scope access —
            // using UserName ("Roland Buergi") here would mismatch the `User/rbuergi/...` path.
            ObjectId = response.UserId ?? response.UserEmail!,
            Name = response.UserName ?? "",
            Email = response.UserEmail!,
            // Stamp the roles captured on the ApiToken at creation time so
            // SecurityService.GetEffectivePermissions can resolve permissions via
            // its claim-based role path (lines 166-174). Without this, API-token
            // requests against per-node hubs see 0 roles → 0 perms → the
            // IsApiToken gate strips → DENY — because per-node hubs intentionally
            // don't register the synced AccessAssignment query
            // (SecurityServiceExtensions:44-50, recursion avoidance).
            Roles = response.Roles,
            IsApiToken = true,
        };
    }

    /// <summary>
    /// Sends a ValidateTokenRequest to the ApiToken node's hub and returns the response.
    /// The request is routed to ApiToken/{hashPrefix} where the handler validates the token.
    /// Public so tests can use the same flow.
    /// </summary>
    public static async Task<ValidateTokenResponse?> ValidateTokenViaHubAsync(string rawToken, IMessageHub hub)
    {
        try
        {
            var hash = ValidateTokenRequest.HashToken(rawToken);
            var hashPrefix = hash[..12];
            var tokenAddress = new Address("ApiToken", hashPrefix);

            var response = await hub.Observe(
                    new ValidateTokenRequest(rawToken),
                    o => o.WithTarget(tokenAddress))
                .FirstAsync()
                .ToTask();
            return response.Message;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Queries the mesh for a User node whose email matches the authenticated user's email.
    /// Uses ImpersonateAsHub scope since the user context hasn't been set yet at this point.
    /// Returns the MeshNode if found, so we can use its Name (from the system) instead of the claim.
    /// </summary>
    private ValueTask<MeshNode?> TryLoadMeshUserAsync(string email, IMessageHub hub)
    {
        try
        {
            var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
            using (accessService.ImpersonateAsHub(hub))
            {
                var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
                return meshService.QueryAsync<MeshNode>(
                    $"nodeType:User namespace:User content.email:{email} limit:1").FirstOrDefaultAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load mesh user for email {Email}", email);
            return default;
        }
    }

    private AccessContext? ExtractUserContext(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        var email = user.FindFirstValue(ClaimTypes.Email)
                    ?? user.FindFirstValue("email")
                    ?? string.Empty;

        // ObjectId = the User MeshNode's Id (e.g., "rbuergi"), NOT the email.
        // ApiTokenAuthenticationHandler puts UserId in preferred_username
        // explicitly so the principal already carries the resolved username;
        // OIDC providers (Microsoft, Google) use preferred_username for the
        // tenant-scoped UPN. Prefer that, fall back to NameIdentifier (also
        // set by the API token handler), then email as a last resort.
        // Without this, downstream code routes to User/<email> instead of
        // User/<username> ("Delivery failed to User/rbuergi@systemorph.com").
        // CircuitAccessHandler.TryLoadMeshUser then re-resolves email→username
        // when needed (interactive OIDC flows that don't pre-set this claim).
        var objectId = user.FindFirstValue("preferred_username")
                    ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? email;

        return new AccessContext
        {
            Name = user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("name") ?? string.Empty,
            ObjectId = objectId,
            Email = email,
            Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        };
    }
}
