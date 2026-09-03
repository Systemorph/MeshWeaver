using System.Globalization;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Middleware that redirects authenticated users without an Active user node
/// to the onboarding page. Runs after UserContextMiddleware.
///
/// <para>Flow:
/// <list type="bullet">
/// <item><description>No user node (or Transient) → redirect /onboarding</description></item>
/// <item><description>Active node → update AccessContext with username, pass through</description></item>
/// <item><description>Active node but sitting ON /onboarding (bookmark, or a cold-start
/// race that bounced them here before the User catalog hydrated) → redirect home;
/// the profile form is not for an already-onboarded user</description></item>
/// </list>
/// </para>
///
/// <para>The user lookup uses <c>workspace.GetQuery</c> (the canonical synced-query
/// API from <c>SyncedMeshNodeQueries.md</c>). The synced layer bypasses RLS internally
/// (System identity), dedupes by path, gates on Initial, and includes static-node
/// providers — same guarantees as <c>ApiTokenService.GetTokensForUser</c> and
/// <c>AgentChatClient.Initialize</c>. Direct <c>IMeshQueryCore.Query</c> calls
/// from application code are pedestrian queries and were forbidden in 2026-05.</para>
///
/// <para>Internally the lookup is a reactive observable chain
/// (<c>workspace.GetQuery</c> → <c>Where</c> → <c>Take(1)</c> → <c>Timeout</c>);
/// the single <c>await</c> at the middleware boundary is unavoidable because
/// ASP.NET Core's <c>RequestDelegate</c> is Task-based.</para>
/// </summary>
public class OnboardingMiddleware(RequestDelegate next, ILogger<OnboardingMiddleware> logger)
{
    /// <summary>
    /// Hard cap on the user-node lookup. Sized for cold start: the User
    /// catalog partition can take 5–10s to hydrate on a fresh portal
    /// process, and the previous 5s budget routinely bounced legitimate
    /// users to <c>/onboarding</c> right after a restart. Bumped to 20s
    /// so the timeout is reserved for genuinely-pathological cases (mesh
    /// down, query layer wedged) rather than cold-start hydration race.
    /// </summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// If the FIRST <see cref="QueryChangeType.Initial"/> snapshot is empty,
    /// resubscribe once after this delay before giving up. Covers the case
    /// where the catalog grain replied to the subscription with an empty
    /// pre-hydration snapshot but never fires a follow-up Added once
    /// hydration completes (we've seen this with the InMemory catalog when
    /// the partition is loaded synchronously by a different request that
    /// holds the grain lock).
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(750);

    // NOTE: "/onboarding" is deliberately NOT here. It still must never redirect a
    // not-yet-onboarded user back TO onboarding (that would loop), but an ALREADY-
    // onboarded user who lands there must be redirected home — so the page is resolved
    // (not blanket-excluded) and handled explicitly in BuildPipeline via onOnboardingPage.
    private static readonly HashSet<string> ExcludedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/welcome",
        "/login",
        "/auth/",
        "/dev/",
        "/admin/",
        "/_framework",
        "/_content",
        "/_blazor",
        "/static/",
        // Asset fetches must never be bounced to onboarding — a redirect renders as a broken
        // image, not a page. /static covers build assets; /api/content is the access-controlled
        // content route mesh images/PDFs/downloads use (issue #587).
        "/api/content",
        "/favicon.ico",
        // Both MCP endpoint paths. The McpEndpointRoutes front-of-pipeline rewrite normally
        // turns /api/mcp into /mcp before this middleware runs; the explicit /api/mcp entry is
        // defense-in-depth against pipeline reordering — an MCP client bounced to /onboarding
        // cannot follow the redirect.
        "/mcp",
        "/api/mcp",
        "/signin-",
        "/bootstrap",
        "/api/email",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        // Pull the reactive composition all the way up: the user-resolution
        // pipeline (FindUserByEmail → conditional LoadUserRoles → SetContext)
        // is a single observable chain. The only Task bridge is on the line
        // below — ASP.NET's RequestDelegate signature forces Task at this
        // boundary, but everything else stays observable so a slow query
        // layer can't deadlock by awaiting a result the awaiting thread is
        // supposed to publish. The bridge is ObserveCompletion, NOT Rx's
        // .ToTask(): the latter resumes this method INLINE on the thread that
        // signalled — a mesh hub's action block — and then the whole rest of
        // the request pipeline runs there (forbidden since 2026-08-30).
        //
        // No cancellation token: RequestAborted must NOT cut this wait short.
        // The decision below is what tells the pipeline whether to redirect,
        // pass through or answer 503, and abandoning it mid-flight would leave
        // the request with no outcome at all — the LookupTimeout inside
        // BuildPipeline is the bound that belongs here.
        //
        // Outcome semantics:
        //   • Result = "Redirect" — middleware bounces to /onboarding, doesn't
        //     call next.
        //   • Result = "PassThrough" — context updated (or skipped because
        //     unauthenticated / virtual / excluded path); fall through to next.
        //   • Result = "Unavailable" — identity could not be RESOLVED (not: resolved
        //     to "no account"); answers 503 + Retry-After, doesn't call next.
        var decision = (await BuildPipeline(context).FirstAsync()
            .ObserveCompletion(ex => logger.LogWarning(ex,
                "Onboarding: the identity pipeline for {Path} faulted after the request had "
                + "already been answered", context.Request.Path)))!;
        var outcome = decision.Outcome;

        if (outcome == OnboardingOutcome.Unavailable)
        {
            // 🚨 UNAVAILABLE ≠ "you have no account" (issue #637). The lookup that decides
            // whether this user is onboarded reached NO verdict — a storage/query fault, not a
            // fact about the user. Bouncing them to /onboarding would tell a correctly
            // signed-in person that their account does not exist and invite them to create a
            // second one; it is the browser-side twin of answering 401 for a read timeout.
            // Answer retryable instead, and say plainly that signing in again will not help.
            await WriteIdentityUnavailable(context, decision.UnavailableReason);
            return;
        }

        if (outcome == OnboardingOutcome.RedirectHome)
        {
            // Already-onboarded user hit /onboarding — the profile form is not for them.
            // Server-side 302 home, BEFORE the page renders: no circuit dependency, no
            // "Loading…/Redirecting…" spinner, no chance of the form flashing up.
            context.Response.Redirect("/");
            return;
        }

        if (outcome == OnboardingOutcome.Redirect)
        {
            // Carry the page the user was trying to reach into the onboarding URL so
            // the form can send them back there on completion — instead of dumping
            // everyone on "/". The target is always THIS request's own path+query on
            // this host, so it is inherently local (no open-redirect surface), and
            // excluded paths (/login, assets, …) never reach here. A bare
            // "/" carries no returnUrl — onboarding falls back to "/" anyway.
            var target = $"{context.Request.Path}{context.Request.QueryString}";
            var location = string.IsNullOrEmpty(target) || target == "/"
                ? "/onboarding"
                : $"/onboarding?returnUrl={Uri.EscapeDataString(target)}";
            context.Response.Redirect(location);
            return;
        }

        await next(context);
    }

    private enum OnboardingOutcome { PassThrough, Redirect, RedirectHome, Unavailable }

    /// <summary>
    /// What the middleware should do next, plus — for
    /// <see cref="OnboardingOutcome.Unavailable"/> — WHY no identity verdict was reached.
    /// The reason is logged (never shown verbatim: it is engineering detail, and the page a
    /// user sees is localized).
    /// </summary>
    private sealed record OnboardingDecision(OnboardingOutcome Outcome, string? UnavailableReason = null)
    {
        public static readonly OnboardingDecision PassThrough = new(OnboardingOutcome.PassThrough);
        public static readonly OnboardingDecision Redirect = new(OnboardingOutcome.Redirect);
        public static readonly OnboardingDecision RedirectHome = new(OnboardingOutcome.RedirectHome);
        public static OnboardingDecision Unavailable(string reason)
            => new(OnboardingOutcome.Unavailable, reason);
    }

    /// <summary>
    /// Answers a request whose identity could not be RESOLVED with
    /// <c>503 Service Unavailable</c> + <c>Retry-After</c> and a localized, plain-text
    /// explanation (issue #637).
    ///
    /// <para>The wording matters as much as the status: the failure mode this replaces sent a
    /// signed-in user to a sign-up form, so the body says explicitly that the sign-in is fine and
    /// that signing in again will not help. Localized through the one seam
    /// (<c>AccessService.Localize</c> → <see cref="MeshWeaver.Messaging.AccessContext.Locale"/>) —
    /// never ambient <c>CultureInfo</c>. Plain text, not hand-built HTML.</para>
    ///
    /// <para>Internal + static so the response SHAPE is testable over a real
    /// <see cref="HttpContext"/> without an HTTP host: asserting the classification alone would
    /// pass while production still redirected.</para>
    /// </summary>
    internal static async Task WriteIdentityUnavailable(
        HttpContext context, string? reason, AccessService? accessService = null)
    {
        var logger = context.RequestServices?.GetService<ILogger<OnboardingMiddleware>>();
        accessService ??= TryResolveViewer(context, logger);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        // Shared with the API-token challenge so the two retry hints cannot drift apart.
        context.Response.Headers.RetryAfter =
            ApiTokenAuthenticationHandler.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            accessService.Localize("error.identityUnavailable")
            + "\n\n"
            + accessService.Localize("error.identityUnavailableHint"),
            context.RequestAborted);

        // The reason is engineering detail — logged, never rendered.
        logger?.LogWarning(
            "Identity resolution UNAVAILABLE for {Path} ({Reason}) — answering 503 + Retry-After "
            + "instead of bouncing a signed-in user to /onboarding (issue #637)",
            context.Request.Path, reason ?? "(no reason given)");
    }

    /// <summary>
    /// The viewer's <see cref="AccessService"/> for language resolution, or <c>null</c> when this
    /// scope has no portal application (an excluded path, teardown, a bare test context).
    ///
    /// <para><see cref="PortalApplication"/> is genuinely OPTIONAL per scope, so it is resolved with
    /// <c>GetService</c>. <see cref="AccessService"/> is a REQUIRED hub service, so it is resolved
    /// with <c>GetRequiredService</c> — per the repo rule, never <c>GetService</c> plus implicit
    /// trust.</para>
    ///
    /// <para>🚨 The lookup is nonetheless guarded, and the guard is the point:
    /// <see cref="WriteIdentityUnavailable"/> is the code that REPORTS an availability failure, so
    /// it must never BECOME one. A throw here would be turned into a <c>500</c> by ASP.NET —
    /// re-creating, inside the fix, exactly the collapse-into-the-wrong-status this fix exists to
    /// prevent. Nothing is hidden: the failure is logged at Warning, and the 503 plus its reason is
    /// still reported. Only the viewer's LANGUAGE degrades, to the English catalog.</para>
    /// </summary>
    private static AccessService? TryResolveViewer(HttpContext context, ILogger? logger)
    {
        try
        {
            // Hub is non-nullable on a constructed PortalApplication.
            return context.RequestServices?.GetService<PortalApplication>()
                ?.Hub.ServiceProvider.GetRequiredService<AccessService>();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Could not resolve the viewer's AccessService while reporting identity-unavailable — "
                + "falling back to the default language. The 503 itself is unaffected.");
            return null;
        }
    }

    /// <summary>
    /// Builds the reactive onboarding pipeline. Returns an observable that
    /// emits exactly one <see cref="OnboardingDecision"/> describing what the
    /// middleware should do next. Composition is end-to-end reactive — no
    /// intermediate <c>await</c>, no fire-and-forget Subscribe, no
    /// TaskCompletionSource. The single Task bridge lives in
    /// <see cref="InvokeAsync"/>.
    /// </summary>
    private IObservable<OnboardingDecision> BuildPipeline(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
            return Observable.Return(OnboardingDecision.PassThrough);

        // /onboarding is resolved (not blanket-excluded): a not-yet-onboarded user
        // stays on the form, but an already-onboarded one is redirected home.
        var onOnboardingPage = context.Request.Path.StartsWithSegments("/onboarding");
        if (!onOnboardingPage && IsExcludedPath(context.Request.Path))
            return Observable.Return(OnboardingDecision.PassThrough);

        var portalApp = context.RequestServices.GetService<PortalApplication>();
        if (portalApp == null)
            return Observable.Return(OnboardingDecision.PassThrough);

        var accessService = portalApp.Hub.ServiceProvider.GetRequiredService<AccessService>();
        var userContext = accessService.Context ?? accessService.CircuitContext;

        // Skip virtual users — they don't need onboarding.
        if (userContext is not { IsVirtual: false } || string.IsNullOrEmpty(userContext.ObjectId))
            return Observable.Return(OnboardingDecision.PassThrough);

        var email = userContext.Email ?? userContext.ObjectId;
        var workspace = portalApp.Hub.GetWorkspace();

        // Correctness fix + diagnostic (2026-06): we previously short-circuited to
        // PassThrough whenever ObjectId != email, ASSUMING such a session was already
        // onboarded. That stranded any session carrying a username-shaped identity with
        // NO backing User node (a leftover DevLogin cookie, a deleted user, …): the
        // middleware never redirected to /onboarding and Index.razor rendered an empty
        // Activity area (the "blank screen, never onboards" bug). We now ALWAYS resolve
        // the user by email; a missing node ⇒ redirect to onboarding. A genuinely
        // onboarded external-auth session carries ObjectId == email here (the cookie's
        // NameIdentifier is the email), so its FindUserByEmail lookup finds the node and
        // passes through — the only sessions this newly redirects are the stale/unknown
        // ones that SHOULD onboard.
        logger.LogDebug(
            "OnboardingMiddleware: resolving session - ObjectId='{ObjectId}' email='{Email}' isVirtual={IsVirtual} path={Path}",
            userContext.ObjectId, email, userContext.IsVirtual, context.Request.Path);

        // Reactive composition: FindUser → SelectMany → either Redirect (no
        // node / Transient) or LoadRoles → set context → PassThrough.
        return FindUserByEmail(workspace, email, logger)
            .SelectMany(userRead =>
            {
                // 🚨 UNAVAILABLE ≠ "no account" (issue #637). The lookup reached NO verdict —
                // it says nothing about whether this user is onboarded. Bouncing them to
                // /onboarding would report an availability failure as an identity failure and
                // invite a signed-in user to create a second account.
                if (userRead.UnavailableReason is { } userReason)
                    return Observable.Return(OnboardingDecision.Unavailable(userReason));

                var node = userRead.Value;

                // Not onboarded (no node, or only a Transient shell) — a DEFINITIVE verdict.
                if (node == null || node.State == MeshNodeState.Transient)
                {
                    // On /onboarding: stay on the form — NEVER redirect back to
                    // /onboarding (that would loop). Everywhere else: bounce there.
                    if (onOnboardingPage)
                        return Observable.Return(OnboardingDecision.PassThrough);

                    logger.LogInformation(
                        "OnboardingMiddleware: Redirecting to onboarding for {Email} (node={NodeState})",
                        email, node?.State.ToString() ?? "(null — lookup returned no match)");
                    return Observable.Return(OnboardingDecision.Redirect);
                }

                // Onboarded but sitting on /onboarding → the form isn't for them; go home.
                if (onOnboardingPage)
                {
                    logger.LogInformation(
                        "OnboardingMiddleware: {Email} is already onboarded — redirecting off /onboarding to home",
                        email);
                    return Observable.Return(OnboardingDecision.RedirectHome);
                }

                var username = node.Id;
                return LoadUserRoles(workspace, username, logger)
                    .Select(rolesRead =>
                    {
                        // 🚨 Same distinction on the role side (issue #637). Stamping an EMPTY
                        // role set because the grant read timed out silently strips the user's
                        // privileges, and every screen they open then says "Access denied" — an
                        // availability failure reported as an authorization failure. An empty
                        // set is only ever stamped when the read RESOLVED to "no grants".
                        if (rolesRead.UnavailableReason is { } rolesReason)
                            return OnboardingDecision.Unavailable(rolesReason);

                        var updatedContext = userContext with
                        {
                            ObjectId = username,
                            Name = node.Name ?? username,
                            Roles = rolesRead.Value ?? Array.Empty<string>()
                        };
                        // Set per-request context. CircuitAccessHandler handles
                        // per-circuit persistence via CreateInboundActivityHandler.
                        accessService.SetContext(updatedContext);
                        return OnboardingDecision.PassThrough;
                    });
            })
            // Both lookups classify their OWN faults (IdentityRead.Bounded), so this only
            // covers a fault in the surrounding composition — not an identity read.
            .Catch<OnboardingDecision, Exception>(ex =>
            {
                // Non-critical — don't block the request on onboarding check failure.
                logger.LogWarning(ex,
                    "OnboardingMiddleware: Failed to check user node for {UserId}",
                    userContext.ObjectId);
                return Observable.Return(OnboardingDecision.PassThrough);
            });
    }

    /// <summary>
    /// Reactive lookup of the User node by email via the canonical synced query
    /// (<c>workspace.GetQuery</c>). The synced layer dedupes by path, gates on
    /// Initial, includes static providers, and runs queries with System identity
    /// internally — so this RLS-bypassing lookup uses exactly the same machinery
    /// as every other "live mesh node set" consumer in the codebase
    /// (<c>ApiTokenService.GetTokensForUser</c>, <c>AgentChatClient</c>, etc.).
    /// Direct <c>IMeshQueryCore.Query</c> here was a pedestrian-query
    /// antipattern — replaced 2026-05 per <c>SyncedMeshNodeQueries.md</c>.
    ///
    /// <para>Returns <see cref="IObservable{T}"/> rather than <see cref="Task{T}"/>
    /// so the caller composes the chain; the middleware is the single allowed
    /// bridge point (ASP.NET's RequestDelegate is Task-based).</para>
    ///
    /// <para>Robustness: the synced layer's Initial-gating means the first
    /// emission is already the authoritative snapshot — no per-emission Where
    /// filter needed. We Take(1) and bound the read (cold start can take seconds while
    /// the partition hydrates). An empty snapshot is a DEFINITIVE
    /// <c>Resolved(null)</c> → "redirect to /onboarding".</para>
    ///
    /// <para>🚨 A read that does NOT complete within the budget is
    /// <see cref="IdentityReadOutcome{T}.Unavailable"/>, never <c>Resolved(null)</c> (issue
    /// #637). It used to fall back to null, so a storage/query stall told a correctly
    /// signed-in user "you have no account" and bounced them to the sign-up form — an
    /// availability failure reported as an identity failure, and one that re-signing-in
    /// cannot fix. The two facts are kept apart HERE, where the timeout is known; no caller
    /// has to infer it from a null or an exception message.</para>
    /// </summary>
    internal static IObservable<IdentityReadOutcome<MeshNode>> FindUserByEmail(
        IWorkspace workspace, string email, ILogger? logger)
    {
        var query = UserByEmailQuery(email);
        var accessService = workspace.Hub.ServiceProvider.GetService<AccessService>();

        // As System, for the reason given on LoadUserRoles — and this read needs it MORE, being
        // the FIRST in the pipeline: it answers "who is this?" when no identity exists yet, so
        // there is nothing for the per-user filter to be correct about. `limit:1` stays; that is a
        // unique-key point lookup, not a set being truncated.
        //
        // The failure it forecloses is the one this whole file exists to prevent (#637): an
        // RLS-stripped User node is indistinguishable from "no such account", and the caller's
        // response to that is to bounce a signed-in user to /onboarding — inviting them to create
        // a SECOND account.
        //
        // The Defer this replaces is not lost — but that was NOT free, and as first written this
        // comment was wrong. Review caught it: SubscribeScopedObservable invoked the factory with
        // no catch, so a synchronous composition throw escaped Subscribe and bypassed
        // IdentityRead.Bounded's classification, turning "we could not find out" back into an
        // unclassified failure — the #637 collapse. RunAsSystem now mirrors Observable.Defer and
        // reports OnError, which is what makes dropping the explicit Defer safe here.
        return IdentityRead.Bounded(
            accessService.RunAsSystem(() =>
                // Fast path: the shared, cross-request synced-query snapshot. For a user this
                // process has ALREADY seen, this replays the cached hit with no DB round-trip.
                workspace.GetQuery($"auth:userByEmail:{email}", query)
                    .Do(items => logger?.LogDebug(
                        "FindUserByEmail({Email}): synced query emit, items={Count}",
                        email, items.Count()))
                    .Take(1)
                    .Select(items => (MeshNode?)items.FirstOrDefault())
                    .SelectMany(cached => cached is not null
                        ? Observable.Return<MeshNode?>(cached)
                        // A cached HIT is authoritative; a cached MISS is NOT. This lookup is a pathless,
                        // auth-routed one-shot fan-out query, and workspace.GetQuery caches its result
                        // PERMANENTLY (Replay(1).AutoConnect(1)) with no live-delta source in the partitioned
                        // portal (the pg_notify listener is disabled). The middleware itself seeds an EMPTY
                        // snapshot while rendering the pre-onboarding / /onboarding requests — so a user
                        // onboarded afterwards would replay that empty snapshot and get bounced to
                        // /onboarding forever ("cannot advance", until a process restart clears the cache).
                        // On a miss, re-read the source of truth (auth.mesh_nodes) before concluding "no
                        // account". The DB row exists synchronously (the auth-mirror trigger fires inside the
                        // onboarding write), so the authoritative re-read finds the just-onboarded user.
                        : QueryUserByEmailAuthoritative(workspace, query, email, logger))),
            LookupTimeout, $"FindUserByEmail({email})", logger);
    }

    /// <summary>
    /// Authoritative one-shot re-read of the User-by-email lookup that BYPASSES the permanent
    /// synced-query cache. <see cref="IMeshService.Query{T}"/>'s first emission is a fresh
    /// <c>Initial</c> fan-out snapshot straight off <c>auth.mesh_nodes</c>, so a user materialised
    /// after the cache seeded empty is found. Invoked ONLY on a cached miss — the common
    /// already-known-user path keeps its zero-DB cache hit. This is the read-after-write authoritative
    /// path CQRS mandates for a gate that decides whether to bounce a user to onboarding.
    ///
    /// <para>Runs as System (<c>Observable.Using</c> holding an
    /// <c>ImpersonateAsSystem</c> scope for the subscription) so the re-read has the SAME
    /// RLS-bypassing visibility the synced layer uses internally — a not-yet-onboarded caller must
    /// not be denied reading the infrastructure <c>auth</c> schema, or the recovery would never
    /// fire.</para>
    /// </summary>
    private static IObservable<MeshNode?> QueryUserByEmailAuthoritative(
        IWorkspace workspace, string query, string email, ILogger? logger)
    {
        var sp = workspace.Hub.ServiceProvider;
        var meshService = sp.GetRequiredService<IMeshService>();
        var accessService = sp.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
            .Take(1)
            .Select(change => (MeshNode?)change.Items.FirstOrDefault())
            // Information ONLY on actual recovery (the stale-cache condition healed) — for a
            // genuinely-not-onboarded user this path runs on every request, so the still-missing
            // case is Debug to keep production (Loki) log volume down.
            .Do(node =>
            {
                if (node is not null)
                    logger?.LogInformation(
                        "FindUserByEmail({Email}): recovered stale-empty cache via authoritative re-read → {User}",
                        email, node.Id);
                else
                    logger?.LogDebug(
                        "FindUserByEmail({Email}): authoritative re-read still finds no user (not onboarded)",
                        email);
            });
    }

    /// <summary>
    /// Reactive load of the user's PLATFORM role names from the three anchored
    /// <see cref="RoleQueries"/> legs (root, <c>Admin</c>, own partition — #3202) via the
    /// canonical synced query (<c>workspace.GetQuery</c>). Same machinery as
    /// <c>FindUserByEmail</c> — bypasses RLS, dedupes, gates on Initial,
    /// includes static providers. Bearer auth uses this via
    /// <see cref="UserRoleResolver.LoadDbRolesAsync"/> to enrich principals with
    /// DB-resolved roles rather than only the roles stamped on the API token at
    /// creation time.
    ///
    /// <para>🚨 A stalled or faulted read is <see cref="IdentityReadOutcome{T}.Unavailable"/>,
    /// never an empty role set (issue #637). It used to default to no roles, which is
    /// indistinguishable from "this user genuinely has no grants" — so a transient storage
    /// fault silently stripped a user's privileges and every screen they opened answered
    /// "Access denied": an availability failure reported as an authorization failure. An
    /// empty set now means exactly one thing: the read RESOLVED and found no grants.</para>
    /// </summary>
    /// <param name="workspace">Workspace to run the synced grant query on.</param>
    /// <param name="username">The mesh user id whose grants to read.</param>
    /// <param name="logger">Optional logger for the emit/timeout diagnostics.</param>
    /// <param name="budget">
    /// Optional override of the read budget. Production passes nothing and gets the cold-start-sized
    /// <see cref="LookupTimeout"/>; tests pass a short window to reach the unavailable branch
    /// deterministically, mirroring how <c>ApiTokenService</c> exposes <c>ValidationReadTimeout</c>.
    /// </param>
    internal static IObservable<IdentityReadOutcome<IReadOnlyCollection<string>>> LoadUserRoles(
        IWorkspace workspace, string username, ILogger? logger, TimeSpan? budget = null)
    {
        var jsonOptions = workspace.Hub.JsonSerializerOptions;
        var accessService = workspace.Hub.ServiceProvider.GetService<AccessService>();

        // 🚨 As System — the same escape hatch, for the same reason, as
        // SecurityService.ObserveScopeAssignments, which reads these very nodes and is described in
        // WrapWithPerUserRls as doing so "by design".
        //
        // This read DECIDES the viewer's roles, so it is a security-fold read wearing ordinary
        // clothes. `workspace.GetQuery` re-applies per-user RLS at the consumer, and that filter
        // resolves permissions through PermissionEvaluator — which reads AccessAssignment nodes.
        // Running it under the caller's identity therefore makes identity establishment depend on
        // the identity being established, and re-enters the permission fold from the request path.
        // Seeing System, WrapWithPerUserRls short-circuits to the raw upstream: "no per-user
        // filter, no service resolution, NO RECURSION". That edge is what this removes.
        //
        // It grants the viewer nothing. The filter can only ever REMOVE grants they legitimately
        // hold, and a stripped grant is indistinguishable from "no grants" — the empty-role outcome
        // whose consequence the caller above documents as every screen answering "Access denied".
        //
        // RunAsSystem, never `Observable.Using(() => ImpersonateAsSystem(), …)`, whose store and
        // restore land on different threads and leave the subscriber latched (AGENTS.md; the
        // ratchet guard fails the build at any new site).
        return IdentityRead.Bounded(
            accessService.RunAsSystem(() =>
                // The (id, query set) pair is the cache key, so the three anchored legs below get
                // their own process-wide snapshot; the synced layer UNIONs them, deduped by path.
                workspace.GetQuery($"auth:userRoles:{username}", RoleQueries(username))
                    .Do(items => logger?.LogDebug(
                        "LoadUserRoles({User}): synced query emit, items={Count}",
                        username, items.Count()))
                    .Take(1)
                    .Select(items => (IReadOnlyCollection<string>?)FoldRoles(items, jsonOptions))),
            budget ?? LookupTimeout, $"LoadUserRoles({username})", logger);
    }

    /// <summary>
    /// The partition that holds the platform-admin grants — "global admin" has exactly one meaning,
    /// <c>Permission.All</c> at scope <c>Admin</c>, i.e. an <c>AccessAssignment</c> in
    /// <c>Admin/_Access</c> (AGENTS.md → "Global admin"; <c>hub.IsGlobalAdmin()</c> keys off it).
    /// </summary>
    internal const string AdminPartition = "Admin";

    /// <summary>
    /// The account lookup: <c>nodeType:User</c> with no path. That is deliberate and it is PINNED,
    /// not unanchored — <c>UserNodeType</c> registers a <c>QueryRoutingRule</c> that routes a
    /// path-less <c>nodeType:User</c> query to the <c>Auth</c> partition (the auth-mirror of every
    /// User node in the mesh, excluded from cross-schema search), and the Postgres planner consults
    /// that rule BEFORE refusing a query as unanchored. <c>SignInReadsAreAnchoredTest</c> pins that
    /// the rule resolves this exact text on the real mesh configuration.
    /// </summary>
    /// <param name="email">The sign-in email.</param>
    /// <returns>The query string.</returns>
    internal static string UserByEmailQuery(string email) =>
        $"nodeType:User content.email:{email} limit:1";

    /// <summary>
    /// The three ANCHORED reads whose union is the user's platform role set (#3202) — each pinned to
    /// ONE schema, none a mesh-wide fan-out:
    /// <list type="number">
    ///   <item>the ROOT scope — <c>_Access/{id}</c>, the registered global satellite
    ///     (<c>system_access</c>): platform-wide viewer/admin grants;</item>
    ///   <item>the <see cref="AdminPartition"/> — <c>Admin/_Access/{id}</c>: the platform-admin
    ///     grant, the ONE meaning of "global admin";</item>
    ///   <item>the user's OWN partition — <c>{user}/_Access/{id}</c>: the Admin-of-my-own-home
    ///     grant onboarding writes.</item>
    /// </list>
    ///
    /// <para><b>Why not every partition.</b> The read this replaces,
    /// <c>nodeType:AccessAssignment content.accessObject:"{user}" scope:subtree</c>, named no
    /// partition: on Postgres that was a UNION over every partition schema on EVERY request (199
    /// schemas on memex-cloud, ~4 s each under load — #2194), and since MeshWeaver.Plugins #1231
    /// made fan-out opt-in it was REFUSED outright, so every signed-in user got a 503. The obvious
    /// remedy — <c>partitions:all</c> — would restore the fan-out, not remove it.</para>
    ///
    /// <para><b>Why three homes are the COMPLETE answer.</b> <c>AccessContext.Roles</c> is read by
    /// no permission decision: <c>PermissionEvaluator</c> deliberately ignores claim roles (a claim
    /// role used to be a global, undeniable grant — the 2026-08-05 paywall bypass), and its only
    /// consumer in core or MeshWeaver.Plugins is the per-viewer access-cache KEY. What the set
    /// means is "this user's PLATFORM roles", and a platform role is granted in exactly these three
    /// places by contract (<c>Doc/Architecture/AccessControl</c> → "Where to look"). A grant in an
    /// arbitrary space (Editor on <c>acme</c>) is a DATA permission the fold evaluates from the
    /// space's own <c>_Access</c> at check time; folding it into the platform roles was incidental
    /// to the mesh-wide query, not a requirement of any caller.</para>
    ///
    /// <para><b>Why the #2011 deny hazard does not apply.</b> Narrowing a fold read is dangerous
    /// where a DENY may live outside the anchor — a group deny that loses its membership rows fails
    /// OPEN. <see cref="FoldRoles"/> honours no deny at all (it collects non-denied role names and
    /// subtracts nothing), so no scope's deny can be lost by not reading that scope.</para>
    /// </summary>
    /// <param name="username">The mesh user id whose grants to read.</param>
    /// <returns>The three anchored query strings, each stamped complete.</returns>
    internal static string[] RoleQueries(string username) =>
    [
        SecurityQueries.RootAssignmentsFor(username),
        SecurityQueries.PartitionAssignmentsFor(AdminPartition, username),
        SecurityQueries.PartitionAssignmentsFor(username, username),
    ];

    /// <summary>Back-compat overload used by callers that don't yet pass a logger.</summary>
    internal static IObservable<IdentityReadOutcome<IReadOnlyCollection<string>>> LoadUserRoles(
        IWorkspace workspace, string username)
        => LoadUserRoles(workspace, username, logger: null);

    private static IReadOnlyCollection<string> FoldRoles(
        IEnumerable<MeshNode> items, JsonSerializerOptions options)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var accessNode in items)
        {
            if (accessNode.Content == null)
                continue;

            AccessAssignment? assignment = accessNode.Content switch
            {
                AccessAssignment aa => aa,
                JsonElement je => JsonSerializer.Deserialize<AccessAssignment>(
                    je.GetRawText(), options),
                _ => null
            };

            if (assignment == null)
                continue;

            foreach (var r in assignment.Roles.Where(r => !r.Denied && !string.IsNullOrEmpty(r.Role)))
                roles.Add(r.Role);
        }
        return roles.ToList();
    }

    private static bool IsExcludedPath(PathString path)
    {
        var pathValue = path.Value ?? "";
        foreach (var prefix in ExcludedPrefixes)
        {
            if (pathValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
