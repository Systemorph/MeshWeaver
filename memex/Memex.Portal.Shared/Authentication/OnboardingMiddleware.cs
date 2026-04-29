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
    /// Hard cap on the user-node lookup. The reactive chain below uses
    /// <see cref="System.Reactive.Linq.Observable.Timeout{T}"/> to fail loudly
    /// if the query layer never emits — better than silently bouncing the user
    /// to /onboarding because the catalog hadn't surfaced their User node yet.
    /// </summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

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
        // Only check authenticated, non-virtual users
        if (context.User?.Identity?.IsAuthenticated == true && !IsExcludedPath(context.Request.Path))
        {
            var portalApp = context.RequestServices.GetService<PortalApplication>();
            if (portalApp != null)
            {
                var accessService = portalApp.Hub.ServiceProvider.GetRequiredService<AccessService>();
                var userContext = accessService.Context ?? accessService.CircuitContext;

                // Skip virtual users — they don't need onboarding
                if (userContext is { IsVirtual: false } && !string.IsNullOrEmpty(userContext.ObjectId))
                {
                    var email = userContext.Email ?? userContext.ObjectId;

                    // If the context's ObjectId was already resolved to a username
                    // (different from the email), this user was onboarded in the current
                    // session. Skip the query — it may not find newly created nodes
                    // immediately due to routing/caching in the mesh query layer.
                    if (!string.IsNullOrEmpty(email) &&
                        !string.IsNullOrEmpty(userContext.ObjectId) &&
                        userContext.ObjectId != email)
                    {
                        await next(context);
                        return;
                    }

                    // IMeshQueryCore — the unsecured infrastructure-query surface.
                    // VUserHelper and SyncedQueryMeshNodes use this same surface — see
                    // PersistenceExtensions.RegisterMeshQueryCoreOnMeshHub.
                    var meshQuery = portalApp.Hub.ServiceProvider.GetService<IMeshQueryCore>();
                    if (meshQuery != null)
                    {
                        try
                        {
                            var node = await FindUserByEmailAsync(meshQuery, portalApp.Hub, email);

                            if (node == null || node.State == MeshNodeState.Transient)
                            {
                                logger.LogInformation(
                                    "OnboardingMiddleware: Redirecting to onboarding for {Email}",
                                    email);
                                context.Response.Redirect("/onboarding");
                                return;
                            }

                            // Active user — update AccessContext with username (node ID)
                            var username = node.Id;
                            var roles = await LoadUserRolesAsync(meshQuery, portalApp.Hub, username);

                            var updatedContext = userContext with
                            {
                                ObjectId = username,
                                Name = node.Name ?? username,
                                Roles = roles
                            };
                            // Set per-request context only. CircuitAccessHandler handles
                            // per-circuit persistence via CreateInboundActivityHandler.
                            accessService.SetContext(updatedContext);
                        }
                        catch (Exception ex)
                        {
                            // Non-critical — don't block the request on onboarding check failure
                            logger.LogWarning(ex,
                                "OnboardingMiddleware: Failed to check user node for {UserId}",
                                userContext.ObjectId);
                        }
                    }
                }
            }
        }

        await next(context);
    }

    /// <summary>
    /// Reactive lookup of the User node by email. Composes
    /// <see cref="IMeshQueryCore.ObserveQuery{T}"/> →
    /// <see cref="Observable.Where{TSource}"/> (skip Initial empties while the
    /// catalog is still warming) → <see cref="Observable.Take{TSource}(IObservable{TSource}, int)"/>
    /// → <see cref="Observable.Timeout{T}(IObservable{T}, TimeSpan)"/>. The
    /// single <see cref="System.Reactive.Threading.Tasks.TaskObservableExtensions.ToTask{TResult}(IObservable{TResult})"/>
    /// at the bottom bridges to the middleware's Task boundary — every other
    /// step stays observable so a slow query layer doesn't deadlock the
    /// request thread, and a stalled query surfaces as a TimeoutException
    /// instead of a silent /onboarding redirect.
    /// </summary>
    private static Task<MeshNode?> FindUserByEmailAsync(
        IMeshQueryCore meshQuery, IMessageHub hub, string email)
    {
        var request = MeshQueryRequest.FromQuery(
            $"nodeType:User namespace:User content.email:{email} limit:1",
            WellKnownUsers.System);

        return meshQuery.ObserveQuery<MeshNode>(request, hub.JsonSerializerOptions)
            .Where(c => c.ChangeType == QueryChangeType.Initial
                     || c.ChangeType == QueryChangeType.Reset
                     || c.ChangeType == QueryChangeType.Added
                     || c.ChangeType == QueryChangeType.Updated)
            // Initial may be empty before the catalog has loaded the User
            // partition; wait for the first change carrying an item.
            .Where(c => c.Items.Count > 0)
            .Select(c => (MeshNode?)c.Items[0])
            .Take(1)
            .Timeout(LookupTimeout, Observable.Return<MeshNode?>(null))
            .FirstOrDefaultAsync()
            .ToTask();
    }

    /// <summary>
    /// Loads the user's role names from AccessAssignment nodes via the same
    /// reactive surface as <see cref="FindUserByEmailAsync"/>. Initial emission
    /// of the ObserveQuery stream carries every matching access node; we read
    /// it once, fold roles, and return.
    /// </summary>
    private static async Task<IReadOnlyCollection<string>> LoadUserRolesAsync(
        IMeshQueryCore meshQuery, IMessageHub hub, string username)
    {
        try
        {
            var request = MeshQueryRequest.FromQuery(
                $"nodeType:AccessAssignment content.accessObject:\"{username}\" scope:subtree limit:10",
                WellKnownUsers.System);

            // Initial change carries the snapshot — Take(1) completes; if the
            // query layer never emits within LookupTimeout, fall back to empty.
            var initial = await meshQuery.ObserveQuery<MeshNode>(request, hub.JsonSerializerOptions)
                .Where(c => c.ChangeType == QueryChangeType.Initial
                         || c.ChangeType == QueryChangeType.Reset)
                .Take(1)
                .Timeout(LookupTimeout, Observable.Return(new QueryResultChange<MeshNode>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = Array.Empty<MeshNode>(),
                    Timestamp = DateTimeOffset.UtcNow,
                }))
                .FirstAsync()
                .ToTask();

            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var accessNode in initial.Items)
            {
                if (accessNode.Content == null)
                    continue;

                AccessAssignment? assignment = accessNode.Content switch
                {
                    AccessAssignment aa => aa,
                    JsonElement je => JsonSerializer.Deserialize<AccessAssignment>(
                        je.GetRawText(), hub.JsonSerializerOptions),
                    _ => null
                };

                if (assignment == null)
                    continue;

                foreach (var r in assignment.Roles.Where(r => !r.Denied && !string.IsNullOrEmpty(r.Role)))
                    roles.Add(r.Role);
            }

            return roles.ToList();
        }
        catch
        {
            // Non-critical — return empty roles on failure
            return [];
        }
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
