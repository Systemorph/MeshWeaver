using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Xml.Linq;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The crawler plumbing: a real <c>/robots.txt</c> and <c>/sitemap.xml</c>. Without these the
/// Blazor catch-all served the SPA HTML shell on both URLs — a crawler asking for robots.txt got
/// a web page. The sitemap enumerates exactly the ANONYMOUS surface: every top-level node that
/// passes <see cref="AnonymousGate.AllowAnonymous"/> (public covers, the Store, Space landings)
/// plus each store plugin's declared public segments (the marketing brochures). Fail-open to an
/// empty sitemap — a mesh hiccup must never turn into a 500 for a crawler.
/// </summary>
public static class SeoEndpoints
{
    /// <summary>Node types whose top-level mains are sitemap candidates.</summary>
    private static readonly string[] CandidateNodeTypes = ["Store/Plugin", "Store/Catalog", "Space"];

    public static IEndpointRouteBuilder MapSeo(this IEndpointRouteBuilder app)
    {
        app.MapGet("/robots.txt", (HttpContext http) =>
        {
            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            return Results.Text(
                $"""
                 User-agent: *
                 Disallow: /login
                 Disallow: /api/
                 Disallow: /_blazor
                 Disallow: /dev/
                 Sitemap: {baseUrl}/sitemap.xml
                 """, "text/plain");
        }).AllowAnonymous();

        app.MapGet("/sitemap.xml", (IMessageHub hub, HttpContext http, CancellationToken ct) =>
        {
            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            return BuildSitemap(hub, baseUrl)
                .Select(xml => Results.Text(xml, "application/xml"))
                .FirstAsync()
                .ToTask(ct);
        }).AllowAnonymous();

        MapShareCard(app);
        return app;
    }

    /// <summary>
    /// 🚨 THE FALLBACK SHARE CARD — <c>/api/og/{node}.png</c>.
    ///
    /// <para>Generated on demand from the node's own name, description and category so that EVERY
    /// public page has an Open Graph image without anyone authoring one. An authored image always
    /// wins; this is what <see cref="SeoResolver.ExtractImage"/> falls back to.</para>
    ///
    /// <para><b>Gated identically to the SEO head.</b> The card is drawn from
    /// <see cref="SeoResolver.Resolve"/>, which returns null for anything the fail-closed
    /// <see cref="AnonymousGate"/> refuses — so a private node's NAME cannot be lifted out of this
    /// route, and a missing node and a private one answer the same 404. There is no parallel
    /// permission rule here to drift from the page's.</para>
    ///
    /// <para><b>Shared-cacheable on purpose</b> — the one image route where <c>public</c> is
    /// correct. Everything drawn on it is already served to anonymous callers on the page itself,
    /// and crawlers refetch cards aggressively; the strong ETag is the render's own hash, so a
    /// renamed node produces a new card rather than a stale one.</para>
    /// </summary>
    private static void MapShareCard(IEndpointRouteBuilder app) =>
        app.MapGet("/api/og/{**path}", (
            IMessageHub hub, OgCardRenderer renderer, HttpContext http, string path,
            CancellationToken ct) =>
        {
            var nodePath = (path ?? "").Trim('/');
            if (nodePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                nodePath = nodePath[..^4];
            if (nodePath.Length == 0)
                return Task.FromResult(Results.NotFound());

            return SeoResolver.Resolve(hub, nodePath)
                .Select(data => data is null
                    ? Results.NotFound()
                    : CardResult(http, renderer, data))
                .Catch<IResult, Exception>(_ => Observable.Return(Results.NotFound()))
                .FirstAsync()
                .ToTask(ct);
        }).AllowAnonymous();

    private static IResult CardResult(HttpContext http, OgCardRenderer renderer, SeoPageData data)
    {
        var node = data.Node;
        var png = renderer.Render(
            node.Name ?? node.Id,
            data.Description,
            string.IsNullOrWhiteSpace(node.Category) ? node.NodeType : node.Category,
            node.Path);

        var etag = $"\"{Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(png))}\"";
        if (string.Equals(http.Request.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(png, "image/png");
    }

    /// <summary>
    /// The sitemap XML, built reactively: candidate roots from the (System-read) type queries,
    /// each gated through the REAL anonymous permission check, public-segment children of store
    /// plugins verified to exist before listing. Cold; never errors (fail-open to fewer URLs).
    /// </summary>
    public static IObservable<string> BuildSitemap(IMessageHub hub, string baseUrl)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        var adapter = hub.ServiceProvider.GetService<IStorageAdapter>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        if (mesh is null || adapter is null)
            return Observable.Return(Render(baseUrl, []));

        // Candidate enumeration runs as System (an anonymous HTTP entry has no query identity);
        // ANONYMOUS readability is then decided per node by the fail-closed gate — the sitemap
        // can never list more than a logged-out visitor can open.
        var candidates = CandidateNodeTypes
            .Select(type => Observable.Using(
                () => accessService?.ImpersonateAsSystem() ?? System.Reactive.Disposables.Disposable.Empty,
                _ => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{type} is:main limit:500"))
                    .Take(1)
                    .Select(change => change.Items
                        .Where(n => !n.Path.Contains('/'))     // top-level roots only
                        .ToList())))
            .ToObservable().Concat().ToList()
            .Select(lists => lists.SelectMany(l => l).DistinctBy(n => n.Path).ToList());

        return candidates
            .SelectMany(roots => roots.Count == 0
                ? Observable.Return(new List<(MeshNode Node, string Url)>())
                : roots
                    .Select(root => AnonymousGate.AllowAnonymous(hub, root.Path)
                        .Take(1)
                        .SelectMany(allowed => allowed
                            ? PagesOf(adapter, hub, root)
                            : Observable.Return<IReadOnlyList<(MeshNode, string)>>([])))
                    .ToObservable().Concat().ToList()
                    .Select(pages => pages.SelectMany(p => p).ToList()))
            .Select(pages => Render(baseUrl, pages.DistinctBy(p => p.Item2).ToList()))
            .Timeout(TimeSpan.FromSeconds(20))
            .Catch<string, Exception>(_ => Observable.Return(Render(baseUrl, [])));
    }

    // The sitemap pages of one anonymous-readable root: the root itself plus, for store
    // plugins, each declared public segment whose node actually exists (the brochures).
    private static IObservable<IReadOnlyList<(MeshNode, string)>> PagesOf(
        IStorageAdapter adapter, IMessageHub hub, MeshNode root)
    {
        var self = (root, root.Path);
        var segments = PublicSegments(root);
        if (segments.Count == 0)
            return Observable.Return<IReadOnlyList<(MeshNode, string)>>([self]);
        return segments
            .Select(segment => adapter
                .Read($"{root.Path}/{segment}", hub.JsonSerializerOptions)
                .Take(1)
                .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null)))
            .ToObservable().Concat().ToList()
            .Select(children => (IReadOnlyList<(MeshNode, string)>)
                new[] { self }
                    .Concat(children.Where(c => c is not null).Select(c => (c!, c!.Path)))
                    .ToList());
    }

    private static IReadOnlyList<string> PublicSegments(MeshNode root) =>
        root.Content is JsonElement { ValueKind: JsonValueKind.Object } je
            && je.TryGetProperty("publicSegments", out var segs)
            && segs.ValueKind == JsonValueKind.Array
            ? segs.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String)
                .Select(s => s.GetString()!)
                .ToList()
            : [];

    private static string Render(string baseUrl, IReadOnlyList<(MeshNode Node, string Url)> pages)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var urlset = new XElement(ns + "urlset",
            pages.Select(p => new XElement(ns + "url",
                new XElement(ns + "loc", $"{baseUrl}/{p.Url}"),
                p.Node.LastModified == default
                    ? null
                    : new XElement(ns + "lastmod", p.Node.LastModified.UtcDateTime.ToString("yyyy-MM-dd")))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), urlset).ToString();
    }
}
