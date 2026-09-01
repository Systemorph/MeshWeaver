using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Security.Claims;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.AspNetCore.Portal;

/// <summary>
/// ASP.NET Core middleware that resolves the authenticated user identity for each request
/// and sets the <c>AccessService</c> context, falling back to the well-known Anonymous identity
/// for unauthenticated or unresolvable requests.
/// </summary>
/// <param name="next">The next middleware delegate in the pipeline.</param>
/// <param name="logger">Logger for user resolution warnings and errors.</param>
public class UserContextMiddleware(RequestDelegate next, ILogger<UserContextMiddleware> logger)
{
    // Framework/build assets — no user context needed, and for /static none may EXIST.
    //
    // 🚨 /static is excluded again (issue #587). It was un-excluded for #666, when the route still
    // served content collections and its address-based shape posted a GetDataRequest that the
    // never-null PostPipeline guard fail-closed to 500 without an AccessContext. That route is gone:
    // /static now serves nothing but build assets straight out of an assembly manifest — no hub
    // post, no permission evaluation, no identity to resolve. Skipping it here is not an
    // optimisation but the contract: /static must not perform an access check, so it must not have
    // a caller to check. Mesh content moved to /api/content, which is NOT excluded and where the
    // owning hub's Read check runs.
    private static readonly string[] ExcludedPrefixes =
        ["/_framework", "/_content", "/_blazor", "/static/", "/favicon.ico"];

    /// <summary>
    /// Resolves the user identity from OAuth claims or a Bearer token and sets the
    /// <c>AccessService</c> context for the current request before passing to the next middleware.
    /// Framework and /static build-asset paths are bypassed without any identity work; every other
    /// path — including the access-controlled /api/content route — resolves a caller.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        // Skip user resolution for Blazor framework resources and the favicon.
        // These requests never need an AccessContext and resolving it adds unnecessary
        // overhead (hub lookup, mesh query) on every JS/CSS/SignalR resource download.
        // (/static serves build assets only and applies no access check — see ExcludedPrefixes.)
        var path = context.Request.Path.Value ?? "";
        if (ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        // The Blazor shell's per-circuit portal hub when it is registered; the mesh root hub on a
        // portal running without the Blazor shell (Features:Gui:Blazor=false). Identity resolution
        // is an HTTP concern, not a circuit concern: AccessService is the MESH-wide singleton
        // either way (a hosted hub only registers its own when the parent has none), so both paths
        // stamp the same service — the portal hub is merely preferred so SSR scopes reuse their
        // circuit hub. Same shape as ResolveContentCaller in BlazorHostingExtensions.
        var hub = context.RequestServices.GetService<PortalApplication>()?.Hub
                  ?? context.RequestServices.GetRequiredService<IMessageHub>();
        var userService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // 🚨 THE ONLY THING WE KNOW about an ANONYMOUS visitor's language. Everything downstream
        // resolves text off AccessContext.Locale (never an ambient culture — a hub render hops the
        // scheduler and an AsyncLocal culture does not survive it), and an anonymous visitor has no
        // profile to read it from. Without this seed the field is null for EVERY first-time
        // visitor, so "the viewer's language wins for chrome" is inert for exactly the audience a
        // paywall / invite / public course page exists for. Null when the browser asked for nothing
        // we ship — see Locales.Negotiate on why that stays distinguishable from "English".
        var requestLocale = Locales.Negotiate(context.Request.Headers.AcceptLanguage.ToString());

        // Try OAuth first (browser sessions), then Bearer token (MCP / API clients).
        // The bearer-token bridge to Task happens once at the ASP.NET middleware boundary —
        // production surface is IObservable end-to-end (see ExtractFromBearerToken).
        var userContext = ExtractUserContext(context.User);
        if (userContext is null)
        {
            // FirstOrDefaultAsync, NOT FirstAsync: the bearer stream COMPLETES WITHOUT EMITTING
            // when the ValidateTokenRequest can't reach the ApiToken/{hashPrefix} hub (its Orleans
            // grain deactivated on idle → "invalid activation, rejecting now"). FirstAsync would
            // then throw "Sequence contains no elements" — an UNCAUGHT exception → 500. A request
            // with NO bearer token always EMITS the NoToken sentinel, so a null here can only mean
            // "a token WAS presented but validation produced no verdict" — which, like an
            // explicitly-unavailable response, is a retryable infrastructure fault (issue #637).
            //
            // 🚨 The wait is ObserveCompletion, never an Rx-to-Task bridge (maintainer,
            // 2026-08-30: "no ToTask ever"). Rx's bridge resumes this middleware INLINE on the
            // ApiToken hub's own action-block thread, and the whole remainder of the request
            // pipeline then runs there — the exact shape that wedges a partition hub.
            var bearer = await ExtractFromBearerToken(context.Request, hub)
                .FirstOrDefaultAsync()
                .ObserveCompletion(
                    ex => logger.LogWarning(ex,
                        "Bearer token validation faulted AFTER the wait settled for {Path} — "
                        + "reported, not orphaned", path),
                    context.RequestAborted);
            var unavailableReason = bearer is null
                ? "token validation produced no verdict (ApiToken hub unreachable)"
                : bearer.UnavailableReason;
            if (unavailableReason is not null)
            {
                // 🚨 Token validation UNAVAILABLE — retryable, NOT an invalid token. Only requests
                // that actually presented a Bearer mw_ token can reach this branch, i.e. genuine
                // API calls: answer 503 + Retry-After so the client retries with the SAME token
                // instead of treating the degradation as an auth failure and re-authenticating.
                logger.LogWarning(
                    "Bearer token validation UNAVAILABLE for {Path} ({Reason}) — retryable infrastructure fault, NOT an invalid token; answering 503 + Retry-After",
                    path, unavailableReason);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "5";
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(
                    "API token validation is temporarily unavailable — retry shortly. "
                    + "The token was NOT rejected; keep it and retry, do not re-authenticate.");
                return;
            }
            userContext = bearer!.Context;
        }

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
            // Seed the request's language BEFORE the profile lookup, so the profile can override it
            // (MeshUserProjection keeps a set profile field and only falls back to the seed). A
            // signed-in user's STORED preference must win over their browser's header — seeding is
            // for the case where we know nothing, never an override.
            userContext = userContext with { Locale = requestLocale };

            var identityIndexUnavailable = false;
            if (!string.IsNullOrEmpty(userContext.Email))
            {
                var lookup = TryLoadMeshUser(userContext.Email, hub);
                identityIndexUnavailable = lookup.IsUnavailable;
                if (lookup.Node is { } meshUser)
                {
                    // The SAME projection the circuit path applies (see MeshUserProjection): id and
                    // name from the node, time zone and language from the profile when it has them.
                    // This used to take only Id/Name here, which left every server-rendered string
                    // English for a German user — harmless while nothing seeded a locale, and a
                    // visible SSR-vs-circuit split the moment one does.
                    userContext = MeshUserProjection.Apply(userContext, meshUser, hub.JsonSerializerOptions);
                }
                else if (identityIndexUnavailable)
                {
                    // 🚨 NOT "this user has no node" (#974). The index could not answer, so we
                    // fall through to the local-part heuristic below WITHOUT concluding anything
                    // about this user. Logged at Warning because it is an availability signal an
                    // operator should see — a cold or faulted index degrades Name/TimeZone/Locale
                    // resolution for every request until it fills.
                    logger.LogWarning(
                        "UserContextMiddleware: mesh user index UNAVAILABLE for {Email} ({Reason}) — "
                        + "falling back to the email local-part for the partition key. This is NOT "
                        + "evidence that the user is unknown, and must never drive onboarding.",
                        userContext.Email, lookup.UnavailableReason);
                }
            }

            // Defence-in-depth: if anything upstream slipped an email-shaped
            // identifier through (claims provider quirks, Bearer-token path,
            // etc.), refuse to set it. Better anonymous than mis-partitioned.
            if (LooksLikeEmail(userContext.ObjectId))
            {
                // The reason clause is derived from what we actually established (#974): the old
                // text asserted "no mesh User node found yet" even when the index had simply not
                // answered — stating as fact something we never checked.
                logger.LogWarning(
                    "UserContextMiddleware: refusing email-shaped ObjectId '{ObjectId}' "
                    + "for email {Email} ({Reason}). Treating as "
                    + "anonymous so the request can't create a parallel "
                    + "<email> partition. The cache will populate on the next request.",
                    userContext.ObjectId, userContext.Email,
                    identityIndexUnavailable
                        ? "the mesh user index could not answer — whether a User node exists is UNKNOWN"
                        : "no mesh User node carries this email");
                // Never null — treat as Anonymous (least privilege) rather than
                // null, which would fail-close the request at the never-null guard.
                // The language still rides along: refusing an identity is not a reason to serve
                // the wrong language.
                userService.SetContext(AnonymousContext with { Locale = requestLocale });
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
            // 🚨 One dedup gate, two consumers. TrackLogin answers "is this a fresh logon session?"
            // and only that answer may drive the logon actions: this middleware runs on EVERY HTTP
            // request, so an ungated call would re-resolve and re-run the platform's logon actions
            // per page load, per /api call, per SSE frame — the per-request storm the dedup exists
            // to prevent, on the authentication critical path.
            if (TrackLogin(userContext, hub))
                RunLogonActions(userContext, hub);
        }
        else
        {
            // 🚨 NEVER NULL (feedback_access_context_always_set): an
            // unauthenticated request resolves to the well-known Anonymous
            // identity, NOT null. A null context trips the never-null
            // PostPipeline guard and fail-closes EVERY downstream application
            // post (reads, subscribes, layout-area syncs) → the visitor sees a
            // BLANK portal even for public content. This was a root of the
            // prod "portal down" wedge: an invalid/expired token resolves to
            // no userContext, fell here, and the null context blanked the page.
            // Anonymous carries Permission.None by default; RLS still filters
            // reads to exactly what the Anonymous role is granted (public
            // pages), and any write is cleanly rejected — never fail-closed.
            //
            // 🚨 …but it carries the REQUEST'S LANGUAGE. This is the paywall / invite / public
            // course case: the visitor is anonymous BY DEFINITION, so the header is the only
            // statement of language that exists, and dropping it here is what made the
            // viewer's-language-wins decision inert for the audience it was written for.
            userService.SetContext(AnonymousContext with { Locale = requestLocale });
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
    /// Posts a <see cref="TrackActivityRequest"/> with <see cref="ActivityType.Login"/>
    /// for the just-resolved user. Process-level deduped (see <see cref="_loginDedup"/>)
    /// so a request burst from a single user doesn't spam the activity hub. The request
    /// is handled on the dedicated activity-tracking hub (see
    /// <c>ActivityTrackingHub</c> / <c>MeshNodeExtensions.HandleTrackActivity</c>), which
    /// fully observes its own reactive pipeline and surfaces faults at Error — so this is
    /// a plain message dispatch, not a swallowed fire-and-forget. Any synchronous fault
    /// posting the request is logged at Warning (never swallowed) but must not break
    /// authentication, which does not depend on activity tracking.
    /// </summary>
    /// <returns>
    /// <c>true</c> when this request opened a fresh logon session — i.e. it passed the dedup gate.
    /// <see cref="InvokeAsync"/> uses that as the trigger for the platform's logon actions, so the
    /// two stay on ONE gate: a second dedup dictionary would be a second definition of "a logon",
    /// free to drift from this one.
    /// </returns>
    private bool TrackLogin(AccessContext userContext, IMessageHub hub)
    {
        if (string.IsNullOrEmpty(userContext.ObjectId))
            return false;

        var now = DateTimeOffset.UtcNow;
        var last = _loginDedup.GetValueOrDefault(userContext.ObjectId, DateTimeOffset.MinValue);
        if (now - last < LoginDedupWindow)
            return false;
        _loginDedup[userContext.ObjectId] = now;

        try
        {
            hub.Post(new TrackActivityRequest(
                NodePath: userContext.ObjectId,
                UserId: userContext.ObjectId,
                NodeName: userContext.Name,
                NodeType: "User",
                Namespace: ""
            )
            { ActivityType = ActivityType.Login });
        }
        catch (Exception ex)
        {
            // Never swallow silently (feedback_no_bandaids): surface at Warning so a
            // broken tracking-post is visible in Loki. Auth still proceeds — tracking
            // is not on the authentication critical path.
            logger.LogWarning(ex,
                "TrackLogin: failed to post TrackActivityRequest for {ObjectId}", userContext.ObjectId);
        }

        return true;
    }

    /// <summary>
    /// Runs the platform's logon actions for the user who just started a session — the per-user
    /// sibling of <c>INodePostCreationHandler</c>, which can only ever fire at account creation. See
    /// <c>Doc/Architecture/LogonActions</c>.
    ///
    /// <para>🚨 <b>This is the hook point because it is the only one that sees every sign-in.</b>
    /// Cookie/OAuth and Bearer both land here with a fully-resolved mesh identity, which is what a
    /// logon action needs: it acts on the user's own nodes and must therefore run as that user, not
    /// as <c>system-security</c> and not as a hub. The Blazor circuit handler was the obvious
    /// alternative and is worse — it fires per TAB and misses the API surface entirely.</para>
    ///
    /// <para><b>Subscribe and return — never <c>await</c>.</b> This is the `/async` rule-1a shape for
    /// a <c>Task</c>-returning signature that is not ours (ASP.NET middleware): hang the work off the
    /// signal and let the request continue. Authentication must never wait on a migration, and a mesh
    /// that cannot answer must cost a missed migration rather than a failed login. The pipeline is
    /// bounded and self-terminating on the runner's side (its own <c>Timeout</c> + <c>Catch</c>), so
    /// the subscription always completes and roots nothing.</para>
    /// </summary>
    private void RunLogonActions(AccessContext userContext, IMessageHub hub)
    {
        // Optional by design: a host without MeshWeaver.Graph (a bare messaging host, a test
        // fixture) simply has no runner and no logon actions — never a startup failure.
        var runner = hub.ServiceProvider.GetService<LogonActionRunner>();
        if (runner is null)
            return;

        // 🚨 Stream faults are handled FLUENTLY (`/async` rule 1b), not by a try/catch around
        // Subscribe: a fault travelling through the stream arrives later, usually on another thread,
        // and sails straight past a catch block. Observable.Empty means "this did not happen" — the
        // right recovery here, because the OnNext arm must not run for a run that failed.
        //
        // The try/catch below covers ONLY the genuinely SYNCHRONOUS throw from the Subscribe call
        // itself (composing the pipeline, resolving a service inside it). It does NOT cover the
        // stream — the .Catch above does — and it is here because a throw on the authentication
        // path would 500 the request.
        try
        {
            runner.RunFor(userContext)
                .Catch<Unit, Exception>(ex =>
                {
                    logger.LogWarning(ex,
                        "Logon actions failed for {ObjectId}", userContext.ObjectId);
                    return Observable.Empty<Unit>();
                })
                .Subscribe(_ => { });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Logon actions could not be started for {ObjectId}", userContext.ObjectId);
        }
    }

    /// <summary>
    /// Validates a Bearer token by sending a ValidateTokenRequest to the token's hub address.
    /// The ApiToken node type handler validates hash/expiry/revocation and returns user info.
    /// This gives the token holder the exact same access rights as the user who created the token.
    /// </summary>
    /// <summary>
    /// Outcome of resolving a request's Bearer token, keeping the three cases apart
    /// (issue #637): <see cref="NoToken"/> — no Bearer mw_ token was presented at all
    /// (proceed by other means / anonymous); <see cref="Context"/> non-null — the token
    /// validated; <see cref="UnavailableReason"/> non-null — a token WAS presented but
    /// validation could not run (retryable → the caller answers 503, never 401/anonymous);
    /// both null (with a token presented) — definitive invalid → anonymous.
    /// </summary>
    private sealed record BearerTokenResolution(AccessContext? Context, string? UnavailableReason)
    {
        // Immutable write-once constant (NoStaticState permits static readonly constants).
        public static readonly BearerTokenResolution NoToken = new((AccessContext?)null, null);
    }

    private static IObservable<BearerTokenResolution> ExtractFromBearerToken(HttpRequest request, IMessageHub hub)
    {
        var authHeader = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Observable.Return(BearerTokenResolution.NoToken);

        var rawToken = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(ValidateTokenRequest.TokenPrefix))
            return Observable.Return(BearerTokenResolution.NoToken);

        return ValidateTokenViaHub(rawToken, hub)
            .Select(response =>
            {
                // UNAVAILABLE is a fault category, not a token verdict — surface it so
                // InvokeAsync answers 503 + Retry-After instead of degrading a possibly
                // valid token to Anonymous (issue #637).
                if (response is { IsUnavailable: true })
                    return new BearerTokenResolution(null, response.Error ?? "token validation unavailable");

                return response is { Success: true }
                       && !string.IsNullOrEmpty(response.UserId)
                       && response.UserId.IndexOf('@') < 0
                    ? new BearerTokenResolution(new AccessContext
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
                        // 🚨 NOT AUTHORITY. These are the role ids captured on the ApiToken node
                        // when the token was MINTED, and PermissionEvaluator reads them nowhere:
                        // a token's permissions are folded from the live AccessAssignment /
                        // PartitionAccessPolicy nodes on the target path exactly like a browser
                        // session's, and its API capability from that path's own public grant and
                        // policy cap (PermissionEvaluator.PublicSurfaceCarriesApi). They are
                        // stamped only as a diagnostic and because AccessControlPipeline uses a
                        // non-empty Roles list as its cue to restore the sender's AccessContext on
                        // a receiving hub.
                        //
                        // The comment this replaces claimed the stamp was load-bearing — "per-node
                        // hubs intentionally don't register the synced AccessAssignment query
                        // (recursion avoidance), so without this a token sees 0 roles → 0 perms →
                        // DENY". That stopped being true when the fold moved onto the process-wide
                        // IMeshNodeStreamCache: AddRowLevelSecurity registers no synced query at
                        // all any more, every hub (mesh and per-node alike) runs the SAME
                        // PermissionEvaluator over the SAME cached queries, and claim roles were
                        // removed from node permissions outright by the 2026-08-05 paywall fix.
                        // The stale rationale outlived the mechanism it described by months and
                        // kept a mint-time snapshot in the authority path — see
                        // Doc/Architecture/AccessControl → "API tokens and the Api capability".
                        Roles = response.Roles,
                        IsApiToken = true,
                    }, null)
                    // Definitive negative verdict (unknown/mismatch/revoked/expired) —
                    // fail closed to anonymous, as before.
                    : new BearerTokenResolution(null, null);
            });
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
        // authenticated user (chronic token-forwarding failure, prod 2026-06-18). Run it as System
        // (Permission.All — NOT ImpersonateAsHub, whose hub address has no Read on the token node).
        //
        // 🚨 RunAsSystem, never Observable.Using (#1790). The impersonation is DELIBERATE and stays
        // — what must not stay is the AsyncLocal write. Observable.Using opens the scope on the
        // SUBSCRIBING thread, which here is the ASP.NET REQUEST thread (InvokeAsync subscribes this
        // through .ToTask()), and disposes it when the ApiToken hub answers — a different thread.
        // The request thread was therefore left holding `system-security` for the rest of the
        // pipeline: the `existing.Email == userContext.Email` reuse branch just below reads that
        // latched context, so a token whose record carries no email matched the System context's
        // null email and the whole request ran with Permission.All. RunAsSystem keeps the scope
        // across the cold Observe's Subscribe (where the post is stamped) and leaves it on the way
        // out of that same Subscribe.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService.RunAsSystem(
                () => hub.Observe(
                        new ValidateTokenRequest(rawToken),
                        o => o.WithTarget(tokenAddress))
                    .Select(d => (ValidateTokenResponse?)d.Message))
            // Fail closed (no identity) but NEVER silently — and NEVER as "invalid":
            // an infrastructure fault here reached no verdict about the token, so it
            // is surfaced as an UNAVAILABLE response (IsUnavailable=true). Callers
            // (ExtractFromBearerToken → InvokeAsync) turn that into a retryable 503
            // instead of the anonymous degradation a definitive invalid token gets —
            // collapsing the two made silo degradations indistinguishable from
            // forged tokens (issue #637).
            .Catch((Exception ex) =>
            {
                hub.ServiceProvider.GetService<ILogger<UserContextMiddleware>>()
                    ?.LogWarning(ex,
                        "Token validation via {TokenAddress} faulted — validation UNAVAILABLE (retryable), NOT an invalid token (hashPrefix={HashPrefix})",
                        tokenAddress, hashPrefix);
                return Observable.Return<ValidateTokenResponse?>(
                    ValidateTokenResponse.Unavailable($"Token validation faulted: {ex.GetType().Name}"));
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
    /// <summary>
    /// Resolves the ACCESS CONTEXT of an HTTP caller for surfaces this middleware deliberately
    /// SKIPS (<see cref="ExcludedPrefixes"/> — <c>/static/…</c> above all). Those endpoints post
    /// hub messages of their own, and a post with no identity is refused by the never-null
    /// PostPipeline guard — which is invisible same-silo (local delivery) and fatal cross-silo:
    /// with 2 replicas, ~half of all /static requests died with "AccessContext must never be null"
    /// (#694), because the identity was never resolved for the request in the first place.
    ///
    /// <para>Same rules as the middleware path, NEVER null: authenticated → claims context with the
    /// mesh User node's Id as ObjectId (cache-resolved; an email-shaped ObjectId is REFUSED —
    /// better anonymous than mis-partitioned); otherwise — or on any resolution fault — the
    /// well-known ANONYMOUS context, whose permissions are exactly the Anonymous grants
    /// (public covers and declared public segments). Fail-closed by construction: this can widen
    /// nothing, it only names who is asking so RLS can answer.</para>
    /// </summary>
    public static AccessContext ResolveHttpCaller(
        ClaimsPrincipal? user, IServiceProvider services, ILogger? logger = null)
    {
        try
        {
            var ctx = user is null ? null : ExtractUserContext(user);
            if (ctx is null)
                return AnonymousContext;
            if (!string.IsNullOrEmpty(ctx.Email))
            {
                var lookup = services.GetService<UserIdentityCache>()?.Lookup(ctx.Email)
                             ?? UserIdentityLookup.Unknown;
                if (lookup.Node is { } meshUser)
                    ctx = ctx with { ObjectId = meshUser.Id, Name = meshUser.Name ?? meshUser.Id };
                else if (lookup.IsUnavailable)
                    // Named as an availability failure, not a missing user (#974). The claims
                    // context still stands — this only means the mesh Id/Name could not be
                    // substituted for the claim-derived ones on THIS request.
                    logger?.LogWarning(
                        "ResolveHttpCaller: mesh user index UNAVAILABLE for {Email} ({Reason}) — "
                        + "keeping the claim-derived identity; this is NOT evidence the user is unknown.",
                        ctx.Email, lookup.UnavailableReason);
            }
            if (LooksLikeEmail(ctx.ObjectId))
            {
                logger?.LogWarning(
                    "ResolveHttpCaller: refusing email-shaped ObjectId {ObjectId}; treating as anonymous.",
                    ctx.ObjectId);
                return AnonymousContext;
            }
            return ctx;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "ResolveHttpCaller failed; treating the request as anonymous.");
            return AnonymousContext;
        }
    }

    /// <summary>
    /// Classified email → mesh User lookup (issue #974). Returns the cache's tri-state so the
    /// caller can tell "no such user" from "the index cannot answer" — the two used to be the
    /// same <c>null</c>, and "user unknown" is the input that drives onboarding.
    /// </summary>
    private UserIdentityLookup TryLoadMeshUser(string email, IMessageHub hub)
    {
        try
        {
            var cache = hub.ServiceProvider.GetRequiredService<UserIdentityCache>();
            return cache.Lookup(email);
        }
        catch (Exception ex)
        {
            // 🚨 SWEEP (#974): resolving the cache out of the hub's service provider throws on a
            // hub mid-disposal — an availability condition that used to leave here as `null`,
            // i.e. as "this user does not exist". Classify it instead of collapsing it.
            logger.LogWarning(ex, "Failed to load mesh user for email {Email}", email);
            return UserIdentityLookup.Unavailable(
                $"user index lookup faulted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static AccessContext? ExtractUserContext(ClaimsPrincipal user)
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
