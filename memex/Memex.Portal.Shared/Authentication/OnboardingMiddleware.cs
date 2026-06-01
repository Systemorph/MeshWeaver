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
/// </list>
/// </para>
///
/// <para>The user lookup uses <c>workspace.GetQuery</c> (the canonical synced-query
/// API from <c>SyncedMeshNodeQueries.md</c>). The synced layer bypasses RLS internally
/// (System identity), dedupes by path, gates on Initial, and includes static-node
/// providers — same guarantees as <c>ApiTokenService.GetTokensForUser</c> and
/// <c>AgentChatClient.Initialize</c>. Direct <c>IMeshQueryCore.ObserveQuery</c> calls
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

    private static readonly HashSet<string> ExcludedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/onboarding",
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

        if (outcome == OnboardingOutcome.Redirect)
        {
            context.Response.Redirect("/onboarding");
            return;
        }

        await next(context);
    }

    private enum OnboardingOutcome { PassThrough, Redirect }

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
        if (context.User?.Identity?.IsAuthenticated != true || IsExcludedPath(context.Request.Path))
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
                if (node == null || node.State == MeshNodeState.Transient)
                {
                    logger.LogInformation(
                        "OnboardingMiddleware: Redirecting to onboarding for {Email} (node={NodeState})",
                        email, node?.State.ToString() ?? "(null — lookup returned no match)");
                    return Observable.Return(OnboardingOutcome.Redirect);
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
    /// Direct <c>IMeshQueryCore.ObserveQuery</c> here was a pedestrian-query
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
        // Cache id per-email — synced query result snapshot is shared across
        // any concurrent request for the same email. The synced registry holds
        // the entry for the workspace's lifetime; live mesh change events keep
        // the snapshot fresh, so subsequent requests see up-to-date state.
        return workspace.GetQuery(
                $"auth:userByEmail:{email}",
                $"nodeType:User content.email:{email} limit:1")
            .Do(items => logger?.LogDebug(
                "FindUserByEmail({Email}): synced query emit, items={Count}",
                email, items.Count()))
            .Take(1)
            .Select(items => (MeshNode?)items.FirstOrDefault())
            .Timeout(LookupTimeout, Observable.Defer(() =>
            {
                logger?.LogWarning(
                    "FindUserByEmail({Email}): no user node within {Timeout} — falling back to null (will redirect to /onboarding)",
                    email, LookupTimeout);
                return Observable.Return<MeshNode?>(null);
            }));
    }

    /// <summary>Back-compat overload used by callers that don't yet pass a logger.</summary>
    internal static IObservable<MeshNode?> FindUserByEmail(
        IWorkspace workspace, string email)
        => FindUserByEmail(workspace, email, logger: null);

    /// <summary>
    /// Reactive load of the user's role names from AccessAssignment nodes via the
    /// canonical synced query (<c>workspace.GetQuery</c>). Same machinery as
    /// <see cref="FindUserByEmail"/> — bypasses RLS, dedupes, gates on Initial,
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
