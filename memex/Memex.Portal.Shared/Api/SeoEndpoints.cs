using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Xml.Linq;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
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
    ///
    /// <para>🚨 <c>GetRequiredService</c>, unlike <see cref="LateFault"/> above, and the difference
    /// is the point: this sink IS the diagnostic. A null-conditional logger would make the one
    /// signal that a mark is broken silently optional — the exact failure the method exists to
    /// prevent — so a host with no logger factory must say so loudly rather than serve 404s that
    /// mean nothing. Resolved ONCE per request, before the reactive chain, so the cost is not paid
    /// per emission and a missing registration cannot surface as a swallowed 404.</para>
    /// </summary>
    private static Action<Exception> UnrenderableIcon(IMessageHub hub, string nodePath)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SeoEndpoints));
        return ex => logger.LogWarning(
            ex, "The icon of '{Path}' is inline svg that will not render; serving no raster icon "
                + "for it", nodePath);
    }

    /// <summary>Node types whose top-level mains are sitemap candidates (the partition roots).</summary>
    private static readonly ImmutableArray<string> CandidateNodeTypes = ["Store/Plugin", "Store/Catalog", "Space"];

    /// <summary>
    /// 🚨 WHAT COUNTS AS A PAGE below a public root — the node types whose instances are documents
    /// a person reads, as opposed to the data, code, releases and registrations a partition also
    /// holds. A reinsurance plugin's partition carries hundreds of amount types, cashflows, source
    /// files and release markers; none of those is a page, and listing them would bury the twenty
    /// pages that are. Anonymous readability is decided separately, per node, by the gate — this
    /// list only says which readable nodes are worth a search engine's visit.
    /// </summary>
    internal static readonly ImmutableArray<string> PageNodeTypes =
        ["Markdown", "Space", "Store/Plugin", "Store/Catalog", "Edu/Module", "Edu/Page"];

    /// <summary>
    /// Path segments that route to a partition's SATELLITE tables — code, tests, release markers —
    /// never to a page. Underscore segments (<c>_Thread</c>, <c>_Access</c>, <c>_GitSync</c>, …) are
    /// the satellite convention itself.
    /// </summary>
    private static readonly ImmutableHashSet<string> SatelliteSegments =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Source", "Test", "Release");

    /// <summary>Concurrent per-node gate checks while enumerating one root's pages.</summary>
    private const int GateConcurrency = 8;

    /// <summary>
    /// Whether a path below a root can be a page at all: no satellite segment anywhere in it.
    /// Pure; the gate decides readability afterwards.
    /// </summary>
    internal static bool IsPagePath(string path)
        => path.Split('/').All(segment =>
            segment.Length > 0
            && segment[0] != '_'
            && !SatelliteSegments.Contains(segment));

    public static IEndpointRouteBuilder MapSeo(this IEndpointRouteBuilder app)
    {
        app.MapGet("/robots.txt", (HttpContext http, IConfiguration configuration) =>
        {
            var baseUrl = PublicSite.CanonicalBaseUrl(configuration, http.Request);
            // The app host is the same site under a second name: nothing on it is for the index,
            // and the sitemap it points at is the public host's. See PublicSite.
            var rules = PublicSite.IsAppOnlyHost(configuration, http.Request)
                ? "Disallow: /"
                : """
                  Disallow: /login
                  Disallow: /welcome
                  Disallow: /api/
                  Disallow: /_blazor
                  Disallow: /dev/
                  """.TrimEnd();
            return Results.Text(
                $"""
                 User-agent: *
                 {rules}
                 Sitemap: {baseUrl}/sitemap.xml
                 """, "text/plain");
        }).AllowAnonymous();

        // [FromServices] on the hub, explicitly: minimal-API parameter inference classifies an
        // unregistered reference type as the request BODY, so a host that maps these routes
        // without a mesh (a test of robots.txt alone) failed at map time with "Body was inferred".
        app.MapGet("/sitemap.xml", ([FromServices] IMessageHub hub, HttpContext http, IConfiguration configuration, CancellationToken ct) =>
        {
            var baseUrl = PublicSite.CanonicalBaseUrl(configuration, http.Request);
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
            [FromServices] IMessageHub hub, [FromServices] OgCardRenderer renderer, HttpContext http, string path,
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
            [FromServices] IMessageHub hub, HttpContext http, string path, CancellationToken ct) =>
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
            var unrenderable = UnrenderableIcon(hub, nodePath);
            return SeoResolver.Resolve(hub, nodePath)
                .Select(data => data is null
                    ? Results.NotFound()
                    : IconResult(http, data.Node, pixels, unrenderable))
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
    /// each gated through the REAL anonymous permission check, then every page-shaped descendant
    /// of an admitted root, each gated the same way. Cold; never errors (fail-open to fewer URLs).
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
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        if (mesh is null)
            return Observable.Return<IReadOnlyList<PublishedPage>>([]);

        // Candidate enumeration runs as System (an anonymous HTTP entry has no query identity);
        // ANONYMOUS readability is then decided per node by the fail-closed gate — the sitemap
        // can never list more than a logged-out visitor can open.
        // RunAsSystem, never `Observable.Using(() => ImpersonateAsSystem(), …)`: the scope's store
        // and restore must land on the SAME thread (AGENTS.md; #1790), and Using disposes on
        // whichever thread the inner stream terminates on.
        var candidates = CandidateNodeTypes
            .Select(type => accessService.RunAsSystem(() =>
                // Mesh-wide by nature: the sitemap enumerates the partition ROOTS of every
                // partition, so there is no partition to anchor to (#3202 — fan-out is opt-in).
                mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    MeshWideQuery.Declare($"nodeType:{type} is:main limit:500")))
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
                            ? PagesOf(mesh, accessService, hub, root)
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

    /// <summary>
    /// The sitemap pages of one anonymous-readable root: the root itself plus every page-shaped
    /// descendant (<see cref="PageNodeTypes"/>, <see cref="IsPagePath"/>) the
    /// <see cref="AnonymousGate"/> admits — checked PER NODE, because a public course is a root
    /// grant plus a deny on every chapter that is not free, and a commercial plugin's cover can be
    /// public while its content is not. The descendant listing runs as System (a listing is a
    /// valid query use: a stale negative here costs a URL, never a leak — every candidate still
    /// passes the gate before it is listed).
    ///
    /// <para>🚨 This REPLACES the read of each store plugin's declared <c>publicSegments</c>
    /// (#4056). That read went through the raw storage adapter from an anonymous HTTP entry and
    /// came back empty on the partitioned Postgres deployment, so no course chapter was ever in the
    /// sitemap; and it could only ever see one level of one node type, so the documentation tree
    /// was never in it either. The gate is the one definition of "public" and it already encodes
    /// the declared segments as grants and denies — asking it per node is both the fix and the
    /// feature.</para>
    /// </summary>
    private static IObservable<IReadOnlyList<(MeshNode, string)>> PagesOf(
        IMeshService mesh, AccessService? accessService, IMessageHub hub, MeshNode root)
    {
        var self = (root, root.Path);
        var types = string.Join("|", PageNodeTypes.Select(t => $"\"{t}\""));
        return accessService.RunAsSystem(() => mesh.Query<MeshNode>(DescendantsOf(root, types)))
            .Take(1)
            .Select(change => change.Items
                .Where(n => n.Path.Length > root.Path.Length
                            && n.Path.StartsWith(root.Path + "/", StringComparison.Ordinal)
                            && IsPagePath(n.Path[(root.Path.Length + 1)..]))
                .DistinctBy(n => n.Path)
                .ToList())
            .SelectMany(candidates => candidates.Count == 0
                ? Observable.Return<IReadOnlyList<(MeshNode, string)>>([self])
                : candidates
                    // Same boolean projection as the roots: "not public" and "the gate could not
                    // decide" both OMIT the page, which is the fail-closed action (#2901).
                    .Select(node => AnonymousGate.AllowAnonymous(hub, node.Path)
                        .Take(1)
                        .Select(allowed => allowed ? node : null))
                    .Merge(GateConcurrency)
                    .Where(node => node is not null)
                    .ToList()
                    .Select(admitted => (IReadOnlyList<(MeshNode, string)>)
                        new[] { self }
                            .Concat(admitted
                                .OrderBy(n => n!.Path, StringComparer.Ordinal)
                                .Select(n => (n!, n!.Path)))
                            .ToList()))
            .Catch<IReadOnlyList<(MeshNode, string)>, Exception>(
                _ => Observable.Return<IReadOnlyList<(MeshNode, string)>>([self]));
    }

    // `limit:all` — this is an ENUMERATION, so every match comes back (MeshQueryRequest.NoLimit);
    // a stated cap would silently truncate the one list that claims to be complete. The sitemap
    // protocol's own bound is 50,000 URLs per file; that is a sitemap-index job for the day a
    // deployment publishes that many pages, not a reason to clip the query.
    private static MeshQueryRequest DescendantsOf(MeshNode root, string types)
        => MeshQueryRequest.FromQuery(
            $"namespace:\"{root.Path}\" scope:descendants is:main nodeType:{types} limit:all");

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
