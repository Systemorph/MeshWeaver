using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Messaging;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// A NuGet v3 feed over this instance's plugins — the distribution half of
/// <c>Doc/Architecture/PluginPackaging</c>.
///
/// <para><b>Why the portal serves this rather than a package registry.</b> Everything a plugin
/// package is made of already lives here: the node content (GitSync), the compiled assemblies (the
/// bake, in <see cref="MeshWeaver.Mesh.Services.IAssemblyStore"/>), and the released SemVer
/// (<c>PackageManifest.ReleasedVersion</c>, off the module's <c>manifest.lock</c>). Publishing them
/// outward would mean copying all three somewhere else and keeping that copy honest; serving them
/// from here means the feed cannot disagree with what the portal actually runs.</para>
///
/// <para>🚨 <b>Authorization is the instance key, not a feed-specific scheme.</b> A caller presents
/// its <c>mwi_</c> key — as <c>Bearer</c>, or as <c>Basic</c>, because a NuGet client cannot send
/// Bearer — and it resolves to the admin-owned <see cref="PluginGrant"/> that says which packages
/// that install may read. Same gate as <c>/api/plugins</c>, deliberately: a second entitlement path
/// is a second thing to get wrong, and this one is already the one purchases are recorded
/// against.</para>
///
/// <para>Only the <c>PackageBaseAddress</c> resource is advertised. That is all `dotnet restore`
/// needs to resolve an explicit version or a range, and every additional resource (search,
/// registration) is a second index that can fall out of step with the packages themselves.</para>
/// </summary>
public static class PluginFeedEndpoints
{
    /// <summary>Route the feed is mounted at. The service index is <c>{Prefix}/index.json</c>.</summary>
    public const string RoutePrefix = "/api/plugins/nuget/v3";

    /// <summary>Flat-container segment — NuGet's <c>PackageBaseAddress/3.0.0</c> resource.</summary>
    public const string FlatContainer = "flat2";

    /// <summary><see cref="HttpContext.Items"/> key holding the authenticated caller.</summary>
    private const string CallerItemKey = "PluginFeed.Caller";

    /// <summary>Maps the instance-key-gated NuGet v3 feed. Call alongside <c>MapPluginRegistry</c>.</summary>
    public static IEndpointRouteBuilder MapPluginFeed(this IEndpointRouteBuilder endpoints)
    {
        // AllowAnonymous at the ASP.NET layer for the same reason the registry does it: callers are
        // INSTANCES, not signed-in users, so the user auth schemes do not apply. The filter below
        // is the real gate.
        var group = endpoints.MapGroup(RoutePrefix).AllowAnonymous();

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var logger = http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(PluginFeedEndpoints));
            var authenticator = http.RequestServices.GetRequiredService<InstanceRegistryAuthenticator>();

            var caller = await authenticator.Authenticate(http.Request.Headers.Authorization)
                .FirstAsync().ToTask(http.RequestAborted);

            if (caller is null)
            {
                // 🚨 Fails CLOSED, with no anonymous escape hatch — unlike /api/plugins, which
                // keeps one for local dev. A feed hands out compiled assemblies for PAID modules;
                // "open when unconfigured" is how the registry served private sources to anyone who
                // knew the URL (2026-08-06), and there is no reason to repeat it here.
                logger?.LogWarning("Plugin feed: rejected {Path} — no valid instance key presented",
                    http.Request.Path);
                return Results.Json(
                    new { error = "A registered instance key is required (Authorization: Bearer mwi_… or Basic)." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            http.Items[CallerItemKey] = caller;
            return await next(ctx);
        });

        group.MapGet("/index.json", (HttpContext http) => Results.Json(ServiceIndex(http)));

        group.MapGet($"/{FlatContainer}/{{id}}/index.json",
            (HttpContext http, IMessageHub rootHub, string id, CancellationToken ct) =>
                Versions(rootHub, id, ct));

        group.MapGet($"/{FlatContainer}/{{id}}/{{version}}/{{file}}",
            (HttpContext http, IMessageHub rootHub, string id, string version, string file,
                CancellationToken ct) =>
                Package(rootHub, id, version, file, ct));

        return endpoints;
    }

    /// <summary>
    /// The v3 service index. Absolute URLs are built from the REQUEST rather than configuration:
    /// a feed reached through an ingress, a port-forward or a custom domain must advertise the host
    /// the caller actually used, or every follow-up request goes somewhere it cannot reach.
    /// </summary>
    private static object ServiceIndex(HttpContext http)
    {
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}{RoutePrefix}";
        return new
        {
            version = "3.0.0",
            resources = new[]
            {
                new
                {
                    id = $"{baseUrl}/{FlatContainer}/",
                    type = "PackageBaseAddress/3.0.0",
                    comment = "Plugin packages, assembled from this instance's node content and assembly store.",
                },
            },
        };
    }

    /// <summary>
    /// The versions of one package. NuGet lowercases ids on the wire, so the lookup is
    /// case-insensitive; the response lists what this instance can actually serve.
    /// </summary>
    private static Task<IResult> Versions(IMessageHub rootHub, string id, CancellationToken ct) =>
        InstalledPackages(rootHub, ct)
            .Select(packages =>
            {
                var versions = packages
                    .Where(p => string.Equals(p.PackageId, id, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Version)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, NuGetVersionComparer.Instance)
                    .ToArray();

                // 404 rather than an empty list: NuGet treats a 200 with no versions as "the
                // package exists and has none", which it then caches.
                return versions.Length == 0
                    ? Results.NotFound()
                    : Results.Json(new { versions });
            })
            .FirstAsync()
            .ToTask(ct);

    /// <summary>
    /// One package's bytes. Assembled on request from what the portal already holds, then handed
    /// back as a stream.
    /// </summary>
    private static Task<IResult> Package(
        IMessageHub rootHub, string id, string version, string file, CancellationToken ct) =>
        InstalledPackages(rootHub, ct)
            .Select(packages => packages.FirstOrDefault(p =>
                string.Equals(p.PackageId, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase)))
            .Select(match => match is null
                // Genuinely unknown to this instance — the honest 404.
                ? Results.NotFound()
                // 🚨 NOT a 404. The package EXISTS and this instance knows its version; what is
                // missing is the assembly step that reads node content and the assembly store and
                // hands them to NuGetPackageWriter. Answering 404 here would tell a caller the
                // package does not exist, which is false and which NuGet caches — a wrong answer
                // that outlives the gap. 501 says "this instance cannot serve it yet" and is not
                // cached as an absence.
                : Results.Json(
                    new
                    {
                        error = "Package assembly is not wired up on this instance yet — "
                                + $"{match.PackageId} {match.Version} is known but cannot be built. "
                                + "The index and version list are live; the download is not.",
                    },
                    statusCode: StatusCodes.Status501NotImplemented))
            .FirstAsync()
            .ToTask(ct);

    /// <summary>
    /// The packages this instance can serve, from the install records in the <c>Plugins</c>
    /// partition — the same records the catalog reads.
    ///
    /// <para>The version is <c>ReleasedVersion</c>, off the module's <c>manifest.lock</c>. Neither
    /// neighbour substitutes: <c>Version</c> is the whole-repo commit sha and <c>ModuleVersion</c>
    /// is a content hash, which is exact but unordered — it cannot answer "which is newer", which
    /// is the only question a feed is asked.</para>
    /// </summary>
    private static IObservable<IReadOnlyList<FeedPackage>> InstalledPackages(
        IMessageHub rootHub, CancellationToken ct) =>
        rootHub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => (IReadOnlyList<FeedPackage>)c.Items
                .Select(node => node.ContentAs<PackageManifest>(
                    rootHub.JsonSerializerOptions))
                .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.ReleasedVersion))
                .Select(m => new FeedPackage(
                    PackagingManifest.IdPrefix + m!.Id, m.ReleasedVersion!, m.Id))
                .ToArray());

    /// <summary>One servable package: its NuGet id, its released version, and the plugin it is.</summary>
    private sealed record FeedPackage(string PackageId, string Version, string PluginId);
}
