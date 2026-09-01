using System.Reactive.Linq;
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
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The crawler plumbing: a real <c>/robots.txt</c> and <c>/sitemap.xml</c>. Without these the
/// Blazor catch-all served the SPA HTML shell on both URLs — a crawler asking for robots.txt got
/// a web page. The sitemap enumerates exactly the ANONYMOUS surface: every top-level node that
/// passes <see cref="AnonymousGate.AllowAnonymous"/> (public covers, the Store, Space landings)
/// plus each store plugin's declared public segments (the marketing brochures). Fail-open to an
/// empty sitemap — a mesh hiccup must never turn into a 500 for a crawler.
/// </summary>
/// <summary>
/// One page that is live on the public internet: the node, and the path a logged-out visitor
/// reaches it at. Produced by <see cref="SeoEndpoints.EnumeratePublished"/>.
/// </summary>
/// <param name="Node">The published node.</param>
/// <param name="Path">Its mesh path, which is also its public URL path — publishing never moves a
/// node, so this is the same path it has always had.</param>
public sealed record PublishedPage(MeshNode Node, string Path);

public static class SeoEndpoints
{
    /// <summary>
    /// Late-fault sink for this surface's <see cref="ReactiveCompletion.ObserveCompletion{T}(System.IObservable{T}, System.Action{System.Exception}, System.Threading.CancellationToken)"/>
    /// bridges: a fault that lands AFTER the crawler response has already settled cannot change the
    /// answer, but discarding it would hide a mesh read that failed on the way out. The logger is
    /// captured eagerly, because a late fault arrives long after the request scope is gone.
    /// </summary>
    private static Action<Exception> LateFault(IMessageHub hub, string route)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SeoEndpoints));
        return ex => logger?.LogWarning(
            ex, "{Route}: faulted after its HTTP response had already settled", route);
    }

    /// <summary>
    /// Sink for an AUTHORED icon whose markup will not parse. The route answers 404 either way, so
    /// without this line a broken mark and a node with no mark are indistinguishable from outside —
    /// and the broken one is the only one anybody can fix.
    /// </summary>
    private static Action<Exception> UnrenderableIcon(IMessageHub hub, string nodePath)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SeoEndpoints));
        return ex => logger?.LogWarning(
            ex, "The icon of '{Path}' is inline svg that will not render; serving no raster icon "
                + "for it", nodePath);
    }

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
                .ObserveCompletion(LateFault(hub, "/sitemap.xml"), ct)!;
        }).AllowAnonymous();

        MapShareCard(app);
        MapNodeIcon(app);
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
                .ObserveCompletion(LateFault(hub, $"/api/og/{nodePath}"), ct)!;
        }).AllowAnonymous();

    /// <summary>
    /// 🚨 THE RASTER FAVICON — <c>/api/icon/{node}.png?size=N</c>.
    ///
    /// <para><b>Why a portal serves its own favicon as PNG.</b> A node page declares the node's own
    /// icon in its head, and every store-package mark is authored inline <c>&lt;svg&gt;</c>. Safari
    /// renders no SVG favicon at all, so on macOS and iOS the per-content favicon was invisible —
    /// every tab wore the portal mark (issue #2075, item 3). This route renders the SAME svg
    /// <see cref="SeoResolver.ResolveIcon"/> puts in the head, so the two are pictures of one
    /// thing, and <see cref="SeoResolver.ResolveIconLinks"/> declares it beside the svg rather than
    /// instead of it.</para>
    ///
    /// <para><b>Gated identically to the SEO head and the share card.</b> It resolves through
    /// <see cref="SeoResolver.Resolve"/>, which returns null for anything the fail-closed
    /// <see cref="AnonymousGate"/> refuses — so a private node's MARK cannot be lifted out of this
    /// route, and a missing node, a private one and a node with no mark all answer the same 404.
    /// There is no parallel permission rule here to drift from the page's.</para>
    ///
    /// <para><b>404 is the fallback, and nothing ever points at it.</b> A node with no icon of its
    /// own gets no icon link in its head either (<see cref="SeoResolver.ResolveIconLinks"/> returns
    /// empty), so the portal favicon stays — the same honest answer the head has always given.
    /// Redirecting to the site favicon here would look like a fix while telling every consumer that
    /// this node's mark IS the portal's.</para>
    ///
    /// <para><b>Sizes are an allow-list, not a range</b> (<see cref="IconRasterizer.SupportedSizes"/>):
    /// the route is anonymous and shared-cacheable, so a free-form size parameter is an unbounded
    /// number of distinct renders. An unsupported size is a 400 rather than a silent snap to 32 —
    /// a caller asking for something this cannot serve should be told so.</para>
    /// </summary>
    private static void MapNodeIcon(IEndpointRouteBuilder app) =>
        app.MapGet("/api/icon/{**path}", (
            IMessageHub hub, HttpContext http, string path, CancellationToken ct) =>
        {
            var nodePath = (path ?? "").Trim('/');
            if (nodePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                nodePath = nodePath[..^4];
            if (nodePath.Length == 0)
                return Task.FromResult(Results.NotFound());

            var size = IconRasterizer.FaviconSize;
            var requested = http.Request.Query["size"].ToString();
            if (requested.Length > 0
                && (!int.TryParse(requested, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out size)
                    || !IconRasterizer.IsSupportedSize(size)))
                return Task.FromResult(Results.BadRequest(
                    $"size must be one of {string.Join(", ", IconRasterizer.SupportedSizes)}"));

            var pixels = size;
            return SeoResolver.Resolve(hub, nodePath)
                .Select(data => data is null
                    ? Results.NotFound()
                    : IconResult(http, data.Node, pixels, UnrenderableIcon(hub, nodePath)))
                .Catch<IResult, Exception>(_ => Observable.Return(Results.NotFound()))
                .FirstAsync()
                .ObserveCompletion(LateFault(hub, $"/api/icon/{nodePath}"), ct)!;
        }).AllowAnonymous();

    /// <summary>
    /// One node's rasterized mark, or 404 when it carries none this can draw. Internal so the
    /// endpoint's own decision — not a re-implementation of it — is what the tests exercise.
    /// </summary>
    /// <param name="http">The request, for conditional-GET and response headers.</param>
    /// <param name="node">The node, already gated as anonymous-readable by the caller.</param>
    /// <param name="size">The square edge in pixels.</param>
    /// <param name="onUnrenderable">Sink for markup that is present but cannot be drawn — an
    /// AUTHORED icon that fails to parse is a content defect worth a line in the log, not something
    /// to swallow into an indistinguishable 404.</param>
    internal static IResult IconResult(
        HttpContext http, MeshNode node, int size, Action<Exception>? onUnrenderable = null)
    {
        if (SeoResolver.ResolveIconSvg(node) is not { } svg)
            return Results.NotFound();

        byte[]? png;
        try
        {
            png = IconRasterizer.Render(svg, size);
        }
        catch (Exception ex)
        {
            // Malformed authored markup is a 4xx-shaped fact about the CONTENT, not a fault in this
            // route — but it is invisible from outside, so it is reported before the 404 is served.
            onUnrenderable?.Invoke(ex);
            return Results.NotFound();
        }

        if (png is null)
            return Results.NotFound();

        var etag = $"\"{Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(png))}\"";
        if (string.Equals(http.Request.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        // Shared-cacheable for the same reason the share card is: everything drawn here is already
        // served to anonymous callers in the page's own head, and the strong ETag is the render's
        // hash — so a node that changes its mark produces a new icon rather than a stale one.
        http.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(png, "image/png");
    }

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
    public static IObservable<string> BuildSitemap(IMessageHub hub, string baseUrl) =>
        EnumeratePublished(hub)
            .Select(pages => Render(baseUrl, pages.Select(p => (p.Node, p.Path)).ToList()))
            .Catch<string, Exception>(_ => Observable.Return(Render(baseUrl, [])));

    /// <summary>
    /// 🚨 THE ONE DEFINITION OF "PUBLISHED TO THE WEB" — every page a logged-out visitor may open.
    ///
    /// <para>There is no separate flag, and deliberately no <c>Www/</c> namespace: a node is
    /// published because it carries an explicit <b>Anonymous Read grant</b>, and that grant is what
    /// <see cref="AnonymousGate"/> already fails closed on. Moving public content under a path
    /// prefix would rewrite every public URL — every shared link, every canonical, every
    /// <c>og:url</c> — which is the opposite of what publishing well requires.</para>
    ///
    /// <para>So "which nodes are on the internet" is a QUERY, not a location, and this is it. The
    /// sitemap renders it as XML for crawlers; <c>PublishedSettingsTab</c> renders the same list for
    /// a human. Two views, one truth — they cannot drift.</para>
    /// </summary>
    public static IObservable<IReadOnlyList<PublishedPage>> EnumeratePublished(IMessageHub hub)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        var adapter = hub.ServiceProvider.GetService<IStorageAdapter>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        if (mesh is null || adapter is null)
            return Observable.Return<IReadOnlyList<PublishedPage>>([]);

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
                    // Boolean projection on purpose (#2901): a root the gate cannot decide on is
                    // OMITTED from the sitemap, which is the same action as "not public" and the
                    // fail-closed one. Omission states nothing, so there is nothing here to be
                    // dishonest about; see AnonymousGate.AllowAnonymous.
                    .Select(root => AnonymousGate.AllowAnonymous(hub, root.Path)
                        .Take(1)
                        .SelectMany(allowed => allowed
                            ? PagesOf(adapter, hub, root)
                            : Observable.Return<IReadOnlyList<(MeshNode, string)>>([])))
                    .ToObservable().Concat().ToList()
                    .Select(pages => pages.SelectMany(p => p).ToList()))
            // PagesOf yields UNNAMED (MeshNode, string) tuples, so address them positionally.
            .Select(pages => (IReadOnlyList<PublishedPage>)pages
                .DistinctBy(p => p.Item2)
                .Select(p => new PublishedPage(p.Item1, p.Item2))
                .ToList())
            .Timeout(TimeSpan.FromSeconds(20))
            .Catch<IReadOnlyList<PublishedPage>, Exception>(_ =>
                Observable.Return<IReadOnlyList<PublishedPage>>([]));
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
