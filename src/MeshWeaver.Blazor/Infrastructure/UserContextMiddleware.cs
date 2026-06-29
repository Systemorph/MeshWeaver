using System.Collections.Concurrent;
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

/// <summary>
/// ASP.NET Core middleware that resolves the authenticated user identity for each request
/// and sets the <c>AccessService</c> context, falling back to the well-known Anonymous identity
/// for unauthenticated or unresolvable requests.
/// </summary>
/// <param name="next">The next middleware delegate in the pipeline.</param>
/// <param name="logger">Logger for user resolution warnings and errors.</param>
public class UserContextMiddleware(RequestDelegate next, ILogger<UserContextMiddleware> logger)
{
    // Blazor framework files, static assets, and favicon — no user context needed.
    private static readonly string[] ExcludedPrefixes =
        ["/_framework", "/_content", "/_blazor", "/static/", "/favicon.ico"];

    /// <summary>
    /// Resolves the user identity from OAuth claims or a Bearer token and sets the
    /// <c>AccessService</c> context for the current request before passing to the next middleware.
    /// Static-asset paths are bypassed without any identity work.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
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

            // AccessContext.ObjectId MUST be the mesh User node's Id
            // (e.g. "rbuergi"), never an email address or email-shaped string.
            // The partition key is the username; using "rbuergi@systemorph.com"
            // as ObjectId routes the user to a parallel partition that owns
            // none of their data, bypasses every AccessAssignment tied to the
            // canonical username, and (historically) caused stray
            // "<email>" schemas to be created by side-effect writes.
            //
            // Resolution order:
            //   1. Cache lookup by email (UserIdentityCache, fed by the
            //      synced `nodeType:User` query).
            //   2. If that misses but the stripped local part still looks
            //      sane, fall back to it.
            //   3. If we'd otherwise stamp an email-shaped ObjectId, REFUSE:
            //      drop the context entirely so the request is treated as
            //      anonymous. The OnboardingMiddleware / dev login will
            //      provision the User node on a follow-up request and the
            //      cache picks it up.
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

            // Defence-in-depth: if anything upstream slipped an email-shaped
            // identifier through (claims provider quirks, Bearer-token path,
            // etc.), refuse to set it. Better anonymous than mis-partitioned.
            if (LooksLikeEmail(userContext.ObjectId))
            {
                logger.LogWarning(
                    "UserContextMiddleware: refusing email-shaped ObjectId '{ObjectId}' "
                    + "for email {Email} (no mesh User node found yet). Treating as "
                    + "anonymous so the request can't create a parallel "
                    + "<email> partition. The cache will populate on the next request.",
                    userContext.ObjectId, userContext.Email);
                // Never null — treat as Anonymous (least privilege) rather than
                // null, which would fail-close the request at the never-null guard.
                userService.SetContext(AnonymousContext);
                await next(context);
                return;
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
            // 🚨 NEVER NULL (feedback_access_context_always_set): an
            // unauthenticated request resolves to the well-known Anonymous
            // identity, NOT null. A null context trips the never-null
            // PostPipeline guard and fail-closes EVERY downstream application
            // post (reads, subscribes, layout-area syncs) → the visitor sees a
            // BLANK portal even for public content. This was a root of the
            // atioz "portal down" wedge: an invalid/expired token resolves to
            // no userContext, fell here, and the null context blanked the page.
            // Anonymous carries Permission.None by default; RLS still filters
            // reads to exactly what the Anonymous role is granted (public
            // pages), and any write is cleanly rejected — never fail-closed.
            userService.SetContext(AnonymousContext);
        }

        await next(context);
    }

    /// <summary>
    /// Process-level dedup for <see cref="TrackLogin"/>. UserContextMiddleware
    /// runs on EVERY HTTP request — page loads, /api calls, /_blazor connects,
    /// SSE — and was previously firing a <c>TrackActivityRequest</c> per
    /// request. That woke the per-user <c>{userId}/_UserActivity/{userId}</c>
    /// grain on every navigation; in prod 2026-05-24 we measured the grain
    /// activation taking 1.2 s on the critical path of a sub-thread page
    /// load and the activity-tracker handler racing the page render for the
    /// same hub's action block.
    ///
    /// Login is a session-shaped event, not a per-request one — a 5-minute
    /// dedup window is sufficient for the "Recently Viewed / Login history"
    /// dashboard that consumes the records. Subsequent requests within the
    /// window skip the Post entirely; the activity grain stays cold unless
    /// another flow needs it.
    /// </summary>
    // Instance field (NOT static): UserContextMiddleware is a single app-lifetime instance, so the
    // dedup is correctly app-scoped and dies with the app — no process-wide static cache. See
    // NoStaticState.md. (Not exercised by tests; the HTTP pipeline doesn't run under MonolithMeshTestBase.)
    private readonly ConcurrentDictionary<string, DateTimeOffset> _loginDedup = new();
    private static readonly TimeSpan LoginDedupWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The well-known Anonymous identity stamped on every unauthenticated request.
    /// NEVER null: a null <see cref="AccessContext"/> trips the never-null
    /// PostPipeline guard and fail-closes every downstream post, blanking the
    /// portal for visitors. Immutable record → safe to share one instance.
    /// Anonymous has <c>Permission.None</c> by default; RLS grants only what the
    /// Anonymous role is assigned (public content). Not a static cache (NoStaticState.md) —
    /// an immutable write-once constant.
    /// </summary>
    private static readonly AccessContext AnonymousContext = new()
    {
        ObjectId = WellKnownUsers.Anonymous,
        Name = WellKnownUsers.Anonymous,
    };

    /// <summary>
    /// Posts a fire-and-forget <see cref="TrackActivityRequest"/> with
    /// <see cref="ActivityType.Login"/> for the just-resolved user. Process-
    /// level deduped (see <see cref="_loginDedup"/>) so a request burst from
    /// a single user doesn't spam the activity grain. Exits silently on any
    /// failure — auth must never depend on activity tracking.
    /// </summary>
    private void TrackLogin(AccessContext userContext, IMessageHub hub)
    {
        try
        {
            if (string.IsNullOrEmpty(userContext.ObjectId))
                return;

            var now = DateTimeOffset.UtcNow;
            var last = _loginDedup.GetValueOrDefault(userContext.ObjectId, DateTimeOffset.MinValue);
            if (now - last < LoginDedupWindow)
                return;
            _loginDedup[userContext.ObjectId] = now;

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
                                && !string.IsNullOrEmpty(response.UserId)
                                && response.UserId.IndexOf('@') < 0
                ? new AccessContext
                {
                    // ObjectId must be the mesh User.Id (e.g. "rbuergi"), never
                    // the email. Guarded by the `IndexOf('@') < 0` check above:
                    // if the validated token somehow carries an email-shaped
                    // UserId (legacy tokens, malformed records), we refuse the
                    // token rather than fall through to UserEmail. Treating
                    // anonymous is safer than mis-partitioning.
                    ObjectId = response.UserId,
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

        // 🚨 Token validation is the AUTH BOOTSTRAP — it runs BEFORE any user identity exists (that
        // is what it establishes). With no AccessContext the ValidateTokenRequest post is fail-closed
        // by the never-null guard, so it never reaches the ApiToken hub → validation returns null →
        // the user resolves as ANONYMOUS → RLS returns empty → blank "portal is down" for every
        // authenticated user (chronic token-forwarding failure, atioz 2026-06-18). Run it as System
        // (Permission.All — NOT ImpersonateAsHub, whose hub address has no Read on the token node).
        // Observable.Using holds the impersonation across the cold Observe's Subscribe.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using<ValidateTokenResponse?, IDisposable>(
                () => accessService?.ImpersonateAsSystem()
                      ?? System.Reactive.Disposables.Disposable.Empty,
                _ => hub.Observe(
                        new ValidateTokenRequest(rawToken),
                        o => o.WithTarget(tokenAddress))
                    .Select(d => (ValidateTokenResponse?)d.Message))
            // Fail closed (null = not authenticated) but NEVER silently: an
            // infrastructure fault here is otherwise indistinguishable from an
            // invalid token, which makes auth incidents undiagnosable.
            .Catch((Exception ex) =>
            {
                hub.ServiceProvider.GetService<ILogger<UserContextMiddleware>>()
                    ?.LogWarning(ex,
                        "Token validation via {TokenAddress} faulted — treating token as invalid (hashPrefix={HashPrefix})",
                        tokenAddress, hashPrefix);
                return Observable.Return<ValidateTokenResponse?>(null);
            });
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
    /// its first <c>Query</c> emission.
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

    /// <summary>
    /// True when a string still looks like an email address (contains
    /// <c>@</c>). Used as the final guard before stamping an
    /// <see cref="AccessContext.ObjectId"/>; an email-shaped ObjectId is a
    /// load-bearing bug -- it becomes the partition key and routes the
    /// user to a parallel partition that owns none of their data.
    /// </summary>
    private static bool LooksLikeEmail(string? value)
        => !string.IsNullOrEmpty(value) && value.IndexOf('@') >= 0;
}
