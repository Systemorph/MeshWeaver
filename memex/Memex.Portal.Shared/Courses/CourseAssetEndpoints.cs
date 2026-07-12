using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Blazor.Infrastructure; // PortalApplication
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging; // AccessService
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Courses;

/// <summary>
/// <c>GET /assets/{Space}/{path…}</c> — the entitlement-gated course-asset endpoint.
/// Course media (videos) live in the Space's synced GitHub repository
/// (<c>{Space}/_GitSync</c>, <see cref="GitHubSyncConfig"/>), not in the mesh; this endpoint
/// resolves the asset's repo file, applies the entitlement gate (<see cref="CourseAssetGate"/>)
/// and 302-redirects to GitHub's short-lived tokenized <c>download_url</c>
/// (<see cref="CourseAssetService"/>) — the bytes are never proxied through the portal.
///
/// <para><b>Gate</b>: the viewer needs <see cref="Permission.Read"/> on the Space AND — when the
/// course is paid (its <c>{Space}/_Entitlements</c> container has entries) — an entitlement node
/// <c>{Space}/_Entitlements/{viewer}</c>. Course admins (<see cref="Permission.Update"/> on the
/// Space) always pass. 404 when the Space has no GitSync config or the file is not in the repo;
/// 401 (anonymous) / 403 (authenticated) when the gate denies.</para>
///
/// <para><b>Shape</b>: mirrors <see cref="MeshWeaver.Hosting.Blazor">the /static endpoint</see> —
/// the whole resolution is one <see cref="IObservable{T}"/> chain, bridged to
/// <c>Task&lt;IResult&gt;</c> exactly once at the HTTP boundary with
/// <c>FirstAsync().ToTask(RequestAborted)</c>. The viewer identity comes from the
/// <c>AccessService</c> context that <c>UserContextMiddleware</c> stamped for this request
/// (same resolution as <c>GitHubConnectEndpoints</c>); the config/entitlement reads run under
/// the System identity (the gate itself is the protection — the satellites are not
/// viewer-readable in general), while the permission check runs for the viewer explicitly.</para>
/// </summary>
public static class CourseAssetEndpoints
{
    /// <summary>The route prefix of the asset endpoint.</summary>
    public const string RoutePrefix = "/assets";

    /// <summary>The Space's entitlement container segment (<c>{Space}/_Entitlements/{viewer}</c>).</summary>
    public const string EntitlementsSegment = "_Entitlements";

    // Ceiling for the viewer's positive Read/Update grant to surface on the live permission
    // stream (its first emission can be the premature empty seed — same rationale and window
    // as AdminMenuGate.GrantWait). A viewer with no grant pays the full wait and is denied.
    private static readonly TimeSpan GrantWait = TimeSpan.FromSeconds(5);

    // Ceiling for the query Initial snapshots (config + entitlements) — a wedged provider
    // must fail the request loudly (500 via the outer Catch), never hang it.
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromSeconds(10);

    /// <summary>Maps the course-asset endpoint. Call after <c>UseAuthentication</c> (needs the resolved user).</summary>
    public static IEndpointRouteBuilder MapCourseAssets(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(RoutePrefix + "/{**path}", (HttpContext http, string? path) =>
        {
            if (!CourseAssetGate.TryParsePath(path, out var space, out var relativePath))
                return Task.FromResult(Results.NotFound("Expected /assets/{Space}/{path…}."));

            var hub = http.RequestServices.GetRequiredService<IMessageHub>();
            var assets = http.RequestServices.GetRequiredService<CourseAssetService>();
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(CourseAssetEndpoints));

            // Viewer identity — the SAME AccessService instance UserContextMiddleware stamped
            // (the portal hub's; mirrors GitHubConnectEndpoints.ResolveMeshUserId), captured
            // SYNCHRONOUSLY before any async hop (the AsyncLocal does not survive SelectMany).
            var accessService = http.RequestServices.GetService<PortalApplication>()?.Hub
                                    .ServiceProvider.GetService<AccessService>()
                                ?? hub.ServiceProvider.GetService<AccessService>();
            var context = accessService?.Context ?? accessService?.CircuitContext;
            var viewerId = context?.ObjectId;
            var isAuthenticated = !string.IsNullOrEmpty(viewerId)
                                  && context?.IsVirtual != true
                                  && !string.Equals(viewerId, WellKnownUsers.Anonymous, StringComparison.Ordinal);
            if (!isAuthenticated)
                viewerId = WellKnownUsers.Anonymous;

            return ResolveAsset(hub, assets, logger, space, relativePath, viewerId!, isAuthenticated)
                .Catch<IResult, Exception>(ex =>
                {
                    logger.LogWarning(ex, "Course-asset resolution failed for {Space}/{Path}", space, relativePath);
                    return Observable.Return(Results.Problem($"Error resolving course asset: {ex.Message}"));
                })
                .FirstAsync()
                .ToTask(http.RequestAborted);
        });
        return endpoints;
    }

    /// <summary>
    /// The full resolution chain: GitSync config + entitlement entries (System-identity Initial
    /// snapshots) + the viewer's Space permissions → gate decision → tokenized download URL.
    /// </summary>
    private static IObservable<IResult> ResolveAsset(
        IMessageHub hub,
        CourseAssetService assets,
        ILogger logger,
        string space,
        string relativePath,
        string viewerId,
        bool isAuthenticated)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var configPath = $"{space}/{GitHubSyncService.ConfigId}";
        var entitlementsNamespace = $"{space}/{EntitlementsSegment}";

        // The Space's primary GitSync source — absent (or repo-less) means there is nothing
        // to serve for this Space: 404, checked before the gate ("no course here" is not an
        // access question and must not leak differently per viewer).
        var config = InitialSnapshot(meshService, $"path:{configPath}")
            .Select(nodes => nodes
                .FirstOrDefault(n => string.Equals(n.Path, configPath, StringComparison.OrdinalIgnoreCase))
                .ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger));

        // The entitlement entries ({Space}/_Entitlements/{userId}): any entry ⇒ the course is
        // paid; an entry whose id matches the viewer ⇒ the viewer is entitled.
        var entitlements = InitialSnapshot(meshService, $"namespace:{entitlementsNamespace}")
            .Select(nodes => nodes
                .Where(n => string.Equals(n.Namespace, entitlementsNamespace, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Id)
                .ToList());

        // The viewer's effective permissions on the Space — wait for the positive (the stream's
        // first emission can be the premature empty seed), bounded; no positive ⇒ None.
        var permissions = hub.GetEffectivePermissions(space, viewerId)
            .Where(p => p.HasFlag(Permission.Read) || p.HasFlag(Permission.Update))
            .Take(1)
            .Timeout(GrantWait)
            .Catch((Exception _) => Observable.Empty<Permission>())
            .DefaultIfEmpty(Permission.None);

        return Observable
            .CombineLatest(config, entitlements, permissions,
                (cfg, entitled, perms) => (Config: cfg, EntitledIds: entitled, Permissions: perms))
            .Take(1)
            .SelectMany(t =>
            {
                if (t.Config?.RepositoryUrl is not { Length: > 0 } repositoryUrl)
                    return Observable.Return(Results.NotFound(
                        $"No course assets at '{space}' (the Space has no GitHub sync source)."));

                var isPaid = t.EntitledIds.Count > 0;
                var isEntitled = isAuthenticated && t.EntitledIds
                    .Any(id => string.Equals(id, viewerId, StringComparison.OrdinalIgnoreCase));
                var decision = CourseAssetGate.Decide(t.Permissions, isAuthenticated, isPaid, isEntitled);
                if (decision == CourseAssetGate.Decision.NotAuthenticated)
                    return Observable.Return(Results.Unauthorized());
                if (decision == CourseAssetGate.Decision.Forbidden)
                    return Observable.Return(Results.StatusCode(StatusCodes.Status403Forbidden));

                var repoFilePath = CourseAssetGate.MapToRepoPath(t.Config.Subdirectory, relativePath);
                return assets.GetDownloadUrl(repositoryUrl, t.Config.Branch, repoFilePath)
                    .Select(url => url is null
                        ? Results.NotFound($"'{relativePath}' not found in the course repository.")
                        : Results.Redirect(url));
            });
    }

    /// <summary>
    /// One deterministic point-in-time query snapshot under the System identity: the
    /// <see cref="QueryChangeType.Initial"/> (or Reset) emission of <see cref="IMeshService.Query{T}"/>.
    /// System because the gate reads hidden satellites the viewer generally cannot Read — the
    /// endpoint's own decision (not RLS on the satellites) is what protects the asset. Bounded
    /// by <see cref="SnapshotWait"/>; an empty completion (no matching provider) yields an
    /// empty snapshot (→ 404 downstream), never a hang.
    /// </summary>
    private static IObservable<IReadOnlyList<MeshNode>> InitialSnapshot(IMeshService meshService, string query) =>
        meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query, WellKnownUsers.System))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Timeout(SnapshotWait)
            .Select(c => c.Items)
            .DefaultIfEmpty([]);
}
