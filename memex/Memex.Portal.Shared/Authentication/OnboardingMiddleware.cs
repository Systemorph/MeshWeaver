using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Blazor.Infrastructure;
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
        "/favicon.ico",
        "/mcp",
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
        // supposed to publish.
        //
        // Outcome semantics:
        //   • Result = "Redirect" — middleware bounces to /onboarding, doesn't
        //     call next.
        //   • Result = "PassThrough" — context updated (or skipped because
        //     unauthenticated / virtual / excluded path); fall through to next.
        var outcome = await BuildPipeline(context).FirstAsync().ToTask();

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

    private enum OnboardingOutcome { PassThrough, Redirect, RedirectHome }

    /// <summary>
    /// Builds the reactive onboarding pipeline. Returns an observable that
    /// emits exactly one <see cref="OnboardingOutcome"/> describing what the
    /// middleware should do next. Composition is end-to-end reactive — no
    /// intermediate <c>await</c>, no fire-and-forget Subscribe, no
    /// TaskCompletionSource. The single Task bridge lives in
    /// <see cref="InvokeAsync"/>.
    /// </summary>
    private IObservable<OnboardingOutcome> BuildPipeline(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
            return Observable.Return(OnboardingOutcome.PassThrough);

        // /onboarding is resolved (not blanket-excluded): a not-yet-onboarded user
        // stays on the form, but an already-onboarded one is redirected home.
        var onOnboardingPage = context.Request.Path.StartsWithSegments("/onboarding");
        if (!onOnboardingPage && IsExcludedPath(context.Request.Path))
            return Observable.Return(OnboardingOutcome.PassThrough);

        var portalApp = context.RequestServices.GetService<PortalApplication>();
        if (portalApp == null)
            return Observable.Return(OnboardingOutcome.PassThrough);

        var accessService = portalApp.Hub.ServiceProvider.GetRequiredService<AccessService>();
        var userContext = accessService.Context ?? accessService.CircuitContext;

        // Skip virtual users — they don't need onboarding.
        if (userContext is not { IsVirtual: false } || string.IsNullOrEmpty(userContext.ObjectId))
            return Observable.Return(OnboardingOutcome.PassThrough);

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
            .SelectMany(node =>
            {
                // Not onboarded (no node, or only a Transient shell).
                if (node == null || node.State == MeshNodeState.Transient)
                {
                    // On /onboarding: stay on the form — NEVER redirect back to
                    // /onboarding (that would loop). Everywhere else: bounce there.
                    if (onOnboardingPage)
                        return Observable.Return(OnboardingOutcome.PassThrough);

                    logger.LogInformation(
                        "OnboardingMiddleware: Redirecting to onboarding for {Email} (node={NodeState})",
                        email, node?.State.ToString() ?? "(null — lookup returned no match)");
                    return Observable.Return(OnboardingOutcome.Redirect);
                }

                // Onboarded but sitting on /onboarding → the form isn't for them; go home.
                if (onOnboardingPage)
                {
                    logger.LogInformation(
                        "OnboardingMiddleware: {Email} is already onboarded — redirecting off /onboarding to home",
                        email);
                    return Observable.Return(OnboardingOutcome.RedirectHome);
                }

                var username = node.Id;
                return LoadUserRoles(workspace, username, logger)
                    .Select(roles =>
                    {
                        var updatedContext = userContext with
                        {
                            ObjectId = username,
                            Name = node.Name ?? username,
                            Roles = roles
                        };
                        // Set per-request context. CircuitAccessHandler handles
                        // per-circuit persistence via CreateInboundActivityHandler.
                        accessService.SetContext(updatedContext);
                        return OnboardingOutcome.PassThrough;
                    });
            })
            .Catch<OnboardingOutcome, Exception>(ex =>
            {
                // Non-critical — don't block the request on onboarding check failure.
                logger.LogWarning(ex,
                    "OnboardingMiddleware: Failed to check user node for {UserId}",
                    userContext.ObjectId);
                return Observable.Return(OnboardingOutcome.PassThrough);
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
    /// filter needed. We Take(1) and Timeout (cold start can take seconds while
    /// the partition hydrates). Empty snapshot → <c>null</c> → "redirect to
    /// /onboarding".</para>
    /// </summary>
    internal static IObservable<MeshNode?> FindUserByEmail(
        IWorkspace workspace, string email, ILogger? logger)
    {
        var query = $"nodeType:User content.email:{email} limit:1";

        // Fast path: the shared, cross-request synced-query snapshot. For a user this process
        // has ALREADY seen, this replays the cached hit with no DB round-trip.
        return workspace.GetQuery($"auth:userByEmail:{email}", query)
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
                : QueryUserByEmailAuthoritative(workspace, query, email, logger))
            .Timeout(LookupTimeout, Observable.Defer(() =>
            {
                logger?.LogWarning(
                    "FindUserByEmail({Email}): no user node within {Timeout} — falling back to null (will redirect to /onboarding)",
                    email, LookupTimeout);
                return Observable.Return<MeshNode?>(null);
            }));
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

    /// <summary>Back-compat overload used by callers that don't yet pass a logger.</summary>
    internal static IObservable<MeshNode?> FindUserByEmail(
        IWorkspace workspace, string email)
        => FindUserByEmail(workspace, email, logger: null);

    /// <summary>
    /// Reactive load of the user's role names from AccessAssignment nodes via the
    /// canonical synced query (<c>workspace.GetQuery</c>). Same machinery as
    /// <c>FindUserByEmail</c> — bypasses RLS, dedupes, gates on Initial,
    /// includes static providers. Bearer auth uses this via
    /// <see cref="UserRoleResolver.LoadDbRolesAsync"/> to enrich principals with
    /// DB-resolved roles rather than only the roles stamped on the API token at
    /// creation time.
    /// </summary>
    internal static IObservable<IReadOnlyCollection<string>> LoadUserRoles(
        IWorkspace workspace, string username, ILogger? logger)
    {
        var jsonOptions = workspace.Hub.JsonSerializerOptions;

        return workspace.GetQuery(
                $"auth:userRoles:{username}",
                $"nodeType:AccessAssignment content.accessObject:\"{username}\" scope:subtree limit:10")
            .Do(items => logger?.LogDebug(
                "LoadUserRoles({User}): synced query emit, items={Count}",
                username, items.Count()))
            .Take(1)
            .Select(items => FoldRoles(items, jsonOptions))
            .Timeout(LookupTimeout, Observable.Defer(() =>
            {
                logger?.LogWarning(
                    "LoadUserRoles({User}): no snapshot within {Timeout} — defaulting to no roles",
                    username, LookupTimeout);
                return Observable.Return((IReadOnlyCollection<string>)Array.Empty<string>());
            }))
            .Catch<IReadOnlyCollection<string>, Exception>(ex =>
            {
                logger?.LogWarning(ex, "LoadUserRoles({User}) failed — defaulting to no roles", username);
                return Observable.Return((IReadOnlyCollection<string>)Array.Empty<string>());
            });
    }

    /// <summary>Back-compat overload used by callers that don't yet pass a logger.</summary>
    internal static IObservable<IReadOnlyCollection<string>> LoadUserRoles(
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
