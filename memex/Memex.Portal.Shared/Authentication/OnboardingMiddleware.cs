using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
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
/// <para>The user lookup uses <see cref="IMeshQueryCore"/> (the unsecured
/// infrastructure surface — same one VUserHelper / SyncedQueryMeshNodes use).
/// Going through <c>IMeshService</c> applies the ACL filter, but at this point
/// in the pipeline the authenticated user has no mesh roles yet — so the
/// secured query returns nothing and every signed-in user got bounced to
/// /onboarding even when their User node existed.</para>
///
/// <para>Internally the lookup is a reactive observable chain
/// (<c>ObserveQuery</c> → <c>Where</c> → <c>Take(1)</c> → <c>Timeout</c>);
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

        // ObjectId already resolved to a username (different from the email)?
        // This session has been onboarded — skip the query (which would race
        // routing/caching in the mesh query layer for newly created nodes).
        if (!string.IsNullOrEmpty(email)
            && !string.IsNullOrEmpty(userContext.ObjectId)
            && userContext.ObjectId != email)
            return Observable.Return(OnboardingOutcome.PassThrough);

        var meshQuery = portalApp.Hub.ServiceProvider.GetService<IMeshQueryCore>();
        if (meshQuery == null)
            return Observable.Return(OnboardingOutcome.PassThrough);

        // Reactive composition: FindUser → SelectMany → either Redirect (no
        // node / Transient) or LoadRoles → set context → PassThrough.
        return FindUserByEmail(meshQuery, portalApp.Hub, email, logger)
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
                return LoadUserRoles(meshQuery, portalApp.Hub, username, logger)
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
    /// Reactive lookup of the User node by email. Returns
    /// <see cref="IObservable{T}"/> rather than <see cref="Task{T}"/> so the
    /// caller composes the chain rather than bridging to Task in the middle —
    /// awaited Tasks on hub-touching observables can deadlock when the awaited
    /// thread is the one that would publish the result. The middleware is the
    /// single allowed bridge point (ASP.NET's RequestDelegate is Task-based);
    /// it composes <c>FindUserByEmail(...).FirstOrDefaultAsync().ToTask()</c>
    /// at the very edge.
    ///
    /// <para>Built on <see cref="IMeshQueryCore.ObserveQuery{T}"/> so the chain
    /// emits live: Initial may be empty before the catalog finishes loading
    /// the User partition; <see cref="Observable.Where{TSource}"/> skips empty
    /// snapshots and waits for the first change carrying an item. Timeout
    /// falls back to null instead of hanging.</para>
    /// </summary>
    /// <summary>
    /// Public reactive User-by-email lookup for callers outside this
    /// middleware (e.g. <c>ApiTokenAuthenticationHandler</c> needs the
    /// same shape to enrich Bearer logins with DB-resolved roles).
    ///
    /// <para>Robustness shape:</para>
    /// <list type="bullet">
    ///   <item>Subscribe and emit the first non-empty snapshot, whichever
    ///         <see cref="QueryChangeType"/> it carries.</item>
    ///   <item>If the first emission's snapshot is empty (and only empties
    ///         keep arriving), wait <see cref="RetryDelay"/> then
    ///         <em>resubscribe once</em> to the same query — covers the
    ///         catalog-emitted-empty-Initial-but-never-fires-Added cold-start
    ///         pathology that previously bounced real users to
    ///         <c>/onboarding</c> on the first request after restart.</item>
    ///   <item>If the second pass also yields nothing within
    ///         <see cref="LookupTimeout"/>, fall back to <c>null</c> (which
    ///         the middleware reads as "no user node — onboard them").</item>
    /// </list>
    ///
    /// <para>Logging is opt-in via the <paramref name="logger"/> parameter
    /// so the API stays usable from non-DI call sites (the legacy
    /// no-logger overload is preserved for back-compat).</para>
    /// </summary>
    internal static IObservable<MeshNode?> FindUserByEmail(
        IMeshQueryCore meshQuery, IMessageHub hub, string email, ILogger? logger)
    {
        // Post-v10: User identity nodes live at root namespace (id = userId).
        // Pre-v10 layout under namespace=User no longer exists.
        var request = MeshQueryRequest.FromQuery(
            $"nodeType:User content.email:{email} limit:1",
            WellKnownUsers.System);

        IObservable<MeshNode?> SubscribeOnce(string pass) =>
            meshQuery.ObserveQuery<MeshNode>(request, hub.JsonSerializerOptions)
                .Do(c => logger?.LogDebug(
                    "FindUserByEmail({Email}) [{Pass}]: {ChangeType} items={Count}",
                    email, pass, c.ChangeType, c.Items.Count))
                .Where(c => c.Items.Count > 0)
                .Select(c => (MeshNode?)c.Items[0])
                .Take(1);

        // First pass + retry-once on empty. Concat is short-circuited by Take(1)
        // — if the first pass produces a node, the retry observable is never
        // materialised. If the first pass terminates without emitting (it can't
        // here since SubscribeOnce never completes early, but we model the path
        // explicitly via Timeout below), we still get a deterministic null.
        var firstPass = SubscribeOnce("first");
        var retryPass = Observable.Timer(RetryDelay)
            .SelectMany(_ =>
            {
                logger?.LogDebug(
                    "FindUserByEmail({Email}): first-pass snapshot empty after {Delay}, resubscribing",
                    email, RetryDelay);
                return SubscribeOnce("retry");
            });

        return firstPass.Amb(retryPass)
            .Take(1)
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
        IMeshQueryCore meshQuery, IMessageHub hub, string email)
        => FindUserByEmail(meshQuery, hub, email, logger: null);

    /// <summary>
    /// Reactive load of the user's role names from AccessAssignment nodes —
    /// same shape as <see cref="FindUserByEmail"/>. Returns
    /// <see cref="IObservable{T}"/> so the caller composes; the Initial /
    /// Reset change carries the snapshot, fold roles via Select, Take(1) and
    /// Timeout the empty set on failure. No <c>.ToTask()</c>, no <c>await</c>;
    /// the only Task bridge in this file is at the middleware boundary.
    /// </summary>
    /// <summary>
    /// Public reactive role-loader for the same reason
    /// <see cref="FindUserByEmail(IMeshQueryCore, IMessageHub, string, ILogger?)"/>
    /// is public — Bearer auth needs to enrich the principal with DB-resolved
    /// AccessAssignment roles, not just whatever roles were stamped on the
    /// API token at creation time. Same timeout + null-fallback contract.
    /// </summary>
    internal static IObservable<IReadOnlyCollection<string>> LoadUserRoles(
        IMeshQueryCore meshQuery, IMessageHub hub, string username, ILogger? logger)
    {
        var request = MeshQueryRequest.FromQuery(
            $"nodeType:AccessAssignment content.accessObject:\"{username}\" scope:subtree limit:10",
            WellKnownUsers.System);

        return meshQuery.ObserveQuery<MeshNode>(request, hub.JsonSerializerOptions)
            .Do(c => logger?.LogDebug(
                "LoadUserRoles({User}): {ChangeType} items={Count}",
                username, c.ChangeType, c.Items.Count))
            .Where(c => c.ChangeType == QueryChangeType.Initial
                     || c.ChangeType == QueryChangeType.Reset)
            .Take(1)
            .Select(change => FoldRoles(change, hub.JsonSerializerOptions))
            .Timeout(LookupTimeout, Observable.Defer(() =>
            {
                logger?.LogWarning(
                    "LoadUserRoles({User}): no Initial/Reset within {Timeout} — defaulting to no roles",
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
        IMeshQueryCore meshQuery, IMessageHub hub, string username)
        => LoadUserRoles(meshQuery, hub, username, logger: null);

    private static IReadOnlyCollection<string> FoldRoles(
        QueryResultChange<MeshNode> change, JsonSerializerOptions options)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var accessNode in change.Items)
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
