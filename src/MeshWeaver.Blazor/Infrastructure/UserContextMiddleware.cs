using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
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

        // Try OAuth first (browser sessions), then Bearer token (MCP / API clients).
        // The bearer-token bridge to Task happens once at the ASP.NET middleware boundary —
        // production surface is IObservable end-to-end (see ExtractFromBearerToken).
        var userContext = ExtractUserContext(context.User)
                          ?? await ExtractFromBearerToken(context.Request, hub).FirstAsync().ToTask(context.RequestAborted);

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
                var meshUser = TryLoadMeshUser(userContext.Email, hub);
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

            // Track the login event in the activity stream — covers both
            // Bearer and cookie/OAuth uniformly because both flows land here.
            // Fire-and-forget: a missing or mid-restart hub must never break
            // authentication. The handler dedupes on encoded NodePath so
            // repeated logins from the same user just bump the existing
            // record's AccessCount + LastAccessedAt — not a flood of new
            // entries.
            TrackLogin(userContext, hub);
        }
        else
        {
            userService.SetContext(null);
        }

        await next(context);
    }

    /// <summary>
    /// Posts a fire-and-forget <see cref="TrackActivityRequest"/> with
    /// <see cref="ActivityType.Login"/> for the just-resolved user. Exits
    /// silently on any failure — auth must never depend on activity tracking.
    /// </summary>
    private static void TrackLogin(AccessContext userContext, IMessageHub hub)
    {
        try
        {
            if (string.IsNullOrEmpty(userContext.ObjectId))
                return;

            hub.Post(new TrackActivityRequest(
                NodePath: userContext.ObjectId,
                UserId: userContext.ObjectId,
                NodeName: userContext.Name,
                NodeType: "User",
                Namespace: ""
            )
            { ActivityType = ActivityType.Login });
        }
        catch
        {
            // Activity tracking is best-effort.
        }
    }

    /// <summary>
    /// Validates a Bearer token by sending a ValidateTokenRequest to the token's hub address.
    /// The ApiToken node type handler validates hash/expiry/revocation and returns user info.
    /// This gives the token holder the exact same access rights as the user who created the token.
    /// </summary>
    private static IObservable<AccessContext?> ExtractFromBearerToken(HttpRequest request, IMessageHub hub)
    {
        var authHeader = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Observable.Return<AccessContext?>(null);

        var rawToken = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(ValidateTokenRequest.TokenPrefix))
            return Observable.Return<AccessContext?>(null);

        return ValidateTokenViaHub(rawToken, hub)
            .Select(response => response is { Success: true }
                ? new AccessContext
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
                }
                : null);
    }

    /// <summary>
    /// Sends a ValidateTokenRequest to the ApiToken node's hub and returns the response.
    /// The request is routed to ApiToken/{hashPrefix} where the handler validates the token.
    /// Public so tests can use the same flow.
    /// </summary>
    public static IObservable<ValidateTokenResponse?> ValidateTokenViaHub(string rawToken, IMessageHub hub)
    {
        var hash = ValidateTokenRequest.HashToken(rawToken);
        var hashPrefix = hash[..12];
        var tokenAddress = new Address("ApiToken", hashPrefix);

        return hub.Observe(
                new ValidateTokenRequest(rawToken),
                o => o.WithTarget(tokenAddress))
            .Select(d => (ValidateTokenResponse?)d.Message)
            .Catch((Exception _) => Observable.Return<ValidateTokenResponse?>(null));
    }

    /// <summary>
    /// Queries the mesh for a User node whose email matches the authenticated user's email.
    /// Uses ImpersonateAsHub scope since the user context hasn't been set yet at this point.
    /// Returns the MeshNode if found, so we can use its Name (from the system) instead of the claim.
    /// </summary>
    /// <summary>
    /// Synchronous email → mesh User node lookup via the hot
    /// <see cref="UserIdentityCache"/> hub-singleton (no await, no hub-touching
    /// observable bridging). Returns <c>null</c> until the cache has received
    /// its first <c>ObserveQuery</c> emission.
    /// </summary>
    private MeshNode? TryLoadMeshUser(string email, IMessageHub hub)
    {
        try
        {
            var cache = hub.ServiceProvider.GetRequiredService<UserIdentityCache>();
            return cache.TryGetByEmail(email);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load mesh user for email {Email}", email);
            return null;
        }
    }

    private AccessContext? ExtractUserContext(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        var email = user.FindFirstValue(ClaimTypes.Email)
                    ?? user.FindFirstValue("email")
                    ?? string.Empty;

        // ObjectId = the User MeshNode's Id (e.g., "rbuergi") = the user's
        // mesh partition key, NEVER the email. ApiTokenAuthenticationHandler
        // and the dev login put the username in preferred_username; OIDC
        // providers (Microsoft, Google) use preferred_username for the
        // tenant-scoped UPN — which IS email-shaped. Prefer the claim, fall
        // back to NameIdentifier, then email. Whatever we land on, normalise
        // an email-shaped value to its local part: post-v10 the username ==
        // email local-part and the partition is keyed by username, so without
        // this downstream routing targets `rbuergi@systemorph.com` instead of
        // the `rbuergi` partition ("No node found at 'rbuergi@systemorph.com'").
        // The mesh User-node lookup below still wins when the cache has it.
        var objectId = user.FindFirstValue("preferred_username")
                    ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? email;
        objectId = UsernameFromEmail(objectId);

        return new AccessContext
        {
            Name = user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("name") ?? string.Empty,
            ObjectId = objectId,
            Email = email,
            Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        };
    }

    /// <summary>
    /// Normalises an email-shaped identifier to its local part — the post-v10
    /// username / mesh-partition key (e.g. <c>rbuergi@systemorph.com → rbuergi</c>).
    /// Returns the input unchanged when there's no <c>@</c>.
    /// </summary>
    private static string UsernameFromEmail(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var at = value.IndexOf('@');
        return at > 0 ? value[..at] : value;
    }
}
