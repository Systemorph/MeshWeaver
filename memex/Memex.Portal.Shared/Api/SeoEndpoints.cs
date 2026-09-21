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
/// plus each store plugin's declared public segments (the marketing brochures).
///
/// <para>🚨 <b>It does NOT fail open to an empty sitemap.</b> It used to, and that is issue #4751:
/// a well-formed <c>&lt;urlset&gt;</c> with zero <c>&lt;loc&gt;</c> is not a smaller answer, it is
/// the OPPOSITE answer — an affirmative census saying this deployment publishes nothing. The
/// crawler-facing concern the old sentence was protecting against is real and is answered with
/// <b>503 + Retry-After</b>, which is what "ask again later" means on the wire; a 200 that
/// declares zero roots is the one response a crawler cannot tell from the truth. See
/// <see cref="PublishedSurface.AssertsWhatItDidNotCheck"/>.</para>
/// </summary>
/// <summary>
/// One page that is live on the public internet: the node, and the path a logged-out visitor
/// reaches it at. Produced by <see cref="SeoEndpoints.EnumeratePublished"/>.
/// </summary>
/// <param name="Node">The published node.</param>
/// <param name="Path">Its mesh path, which is also its public URL path — publishing never moves a
/// node, so this is the same path it has always had.</param>
public sealed record PublishedPage(MeshNode Node, string Path);

/// <summary>
/// The published surface AND whether the enumeration that produced it actually decided it.
///
/// <para>🚨 <b>The second field exists because a list cannot carry it and every consumer needs
/// it</b> (#4751). <see cref="AnonymousGate"/> answers a TRI-state — granted, denied, or
/// <see cref="PermissionCheckOutcome.IsUndetermined"/> when the permission fold reached no verdict
/// — and <c>SeoEndpoints</c> used to project that onto a bool, on the stated grounds that omitting
/// an undecidable root "states nothing". That is true of ONE root and false of all of them: N
/// omissions that each state nothing compose into a census that states everything. So the reason
/// travels out with the pages, and the consumer decides.</para>
/// </summary>
/// <param name="Pages">Every page a logged-out visitor may open, as far as this run established.</param>
/// <param name="Undecided">Null when every root was decided and nothing faulted; otherwise the
/// first reason a root's publicness could not be established, ready to log.</param>
public sealed record PublishedSurface(IReadOnlyList<PublishedPage> Pages, string? Undecided)
{
    /// <summary>A surface every root was decided for — a census, and still a census when empty.</summary>
    /// <param name="pages">The decided pages.</param>
    public static PublishedSurface Decided(IReadOnlyList<PublishedPage> pages) => new(pages, null);

    /// <summary>
    /// 🚨 <b>THE ONE RULE, and it is deliberately narrow:</b> true only when publishing this list
    /// would ASSERT something the enumeration never established — no pages at all, and at least one
    /// root whose publicness could not be decided.
    ///
    /// <para>A PARTIAL surface is fine and stays a 200: a sitemap is a hint, the protocol never
    /// promised completeness, and dropping one undecidable root out of a thousand costs one URL
    /// until the next crawl. <b>Zero is the only value that reads as a statement</b>, which is why
    /// it is the only one withheld. The rule is also why a genuinely empty portal still answers
    /// 200 with an empty urlset — that IS the truth, and the synthetic probe that fails on it is
    /// then correctly failing.</para>
    /// </summary>
    public bool AssertsWhatItDidNotCheck => Pages.Count == 0 && Undecided is not null;
}

/// <summary>
/// There is no honest sitemap to render: the enumeration decided nothing AND admitted no page, so
/// the only document it could produce is the zero-root census of #4751. Carried as a fault rather
/// than an empty string so that no caller can mistake it for a result — the route turns it into
/// 503, and <c>PublishedSettingsTab</c> turns it into a sentence for a human.
/// </summary>
/// <param name="reason">Why the surface could not be decided.</param>
public sealed class SitemapUndecidedException(string reason)
    : InvalidOperationException(
        "the published surface could not be decided and no page was admitted, so a sitemap would "
        + "declare zero public roots without having checked any: " + reason)
{
    /// <summary>Why the surface could not be decided, without the framing sentence.</summary>
    public string Reason { get; } = reason;
}

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

    /// <summary>
    /// What <c>/api/sitemap.xml</c> actually answers, as ONE function the route calls and a test
    /// can drive — the same shape as <see cref="IconResult"/>, and for the same reason: a test that
    /// re-implements the mapping beside the route can agree with itself while the shipped answer is
    /// wrong. The status code and the <c>Retry-After</c> header are part of the contract here, so
    /// they are reached from the route's own decision or they are not tested at all.
    ///
    /// <para>Both failure arms land in the same place: the deliberate
    /// <see cref="SitemapUndecidedException"/>, and anything the mesh read faulted on.</para>
    /// </summary>
    /// <param name="hub">The hub the enumeration runs against.</param>
    /// <param name="http">The request — the 503 arm writes <c>Retry-After</c> on its response.</param>
    /// <param name="baseUrl">The canonical public host every <c>&lt;loc&gt;</c> is built on.</param>
    internal static IObservable<IResult> SitemapResult(IMessageHub hub, HttpContext http, string baseUrl) =>
        BuildSitemap(hub, baseUrl)
            .Select(xml => Results.Text(xml, "application/xml"))
            .Catch<IResult, Exception>(ex => Observable.Return(SitemapUnavailable(hub, http, ex)));

    /// <summary>
    /// 🚨 THE ANSWER FOR "I COULD NOT CHECK" — 503 with a <c>Retry-After</c>, and a warning naming
    /// the cause.
    ///
    /// <para>Both halves are the fix for #4751. The status code, because 503 is the wire's word
    /// for "ask again later" and a crawler acts on it correctly, whereas a 200 carrying zero
    /// <c>&lt;loc&gt;</c> is indistinguishable from a portal that genuinely publishes nothing. And
    /// the log line, because the old code discarded the exception into <c>_ =&gt;</c> — so nine
    /// failures over 27 hours left NOTHING anywhere naming a cause, which is why the issue could be
    /// measured precisely and diagnosed not at all.</para>
    /// </summary>
    private static IResult SitemapUnavailable(IMessageHub hub, HttpContext http, Exception cause)
    {
        hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SeoEndpoints))
            .LogWarning(
                cause,
                "/sitemap.xml: the published surface could not be enumerated; answering 503 rather "
                + "than a well-formed sitemap that declares zero public roots");
        http.Response.Headers.RetryAfter = "300";
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Node types whose top-level mains are sitemap candidates (the partition roots).</summary>
    private static readonly ImmutableArray<string> CandidateNodeTypes = ["Store/Plugin", "Store/Catalog", "Space"];

    /// <summary>
    /// The root enumeration for one candidate type: every TOP-LEVEL main of that type, mesh-wide,
    /// uncapped.
    ///
    /// <para>🚨 The root filter is pushed DOWN, not applied afterwards (#4080). This used to be
    /// <c>nodeType:{type} is:main limit:500</c> with <c>!Path.Contains('/')</c> on the client, so
    /// once a type had more than 500 mains mesh-wide — every course installed into a user
    /// partition is a <c>Space</c> main, every store plugin copy a <c>Store/Plugin</c> — which
    /// roots landed inside the window was decided by storage order, and a public root silently
    /// dropped out of the sitemap. Nothing errored: the endpoint is fail-open to fewer URLs, which
    /// is exactly what made it invisible. <c>namespace:</c> with an EMPTY value is
    /// <c>namespace = ''</c> on every backend (the home catalog's root leg is the other consumer),
    /// so the storage returns only roots and there is nothing left to cap: the population is
    /// bounded by the number of partitions, not by the number of mains.</para>
    /// </summary>
    internal static string RootCandidateQuery(string nodeType) =>
        MeshWideQuery.Declare($"nodeType:{nodeType} is:main namespace: limit:all");

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
            return SitemapResult(hub, http, baseUrl)
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
    /// <para><b>Gated identically to the SEO head — literally the same call.</b> The card is drawn
    /// from <see cref="SeoResolver.ResolveShareableNode"/>, the ONE predicate the head's own card
    /// block asks: a node the fail-closed <see cref="AnonymousGate"/> admits, or one whose scope opted
    /// in to <see cref="PartitionAccessPolicy.PublicPreview"/>. Anything else is 404 — the same
    /// answer a missing node gets, so the route is still no existence oracle. There is no parallel
    /// permission rule here to drift from the page's, which is the whole reason that predicate is one
    /// function: a head declaring <c>og:image</c> for a page whose picture 404s ships a broken card,
    /// and several unfurlers then drop the preview entirely.</para>
    ///
    /// <para><b>Shared-cacheable on purpose</b> — the one image route where <c>public</c> is
    /// correct. Everything drawn on it is already served to anonymous callers on the page itself,
    /// and crawlers refetch cards aggressively; the strong ETag is the render's own hash, so a
    /// renamed node produces a new card rather than a stale one.</para>
    /// </summary>
    private static void MapShareCard(IEndpointRouteBuilder app)
    {
        // The instance's own card — what a page that is no public node shares with (the home
        // page, a private node): the site name and host, nothing read from the mesh, so there is
        // nothing here the anonymous gate would have to withhold.
        app.MapGet("/api/og.png", ([FromServices] OgCardRenderer renderer, HttpContext http) =>
            PngResult(http, renderer.RenderSite(http.Request.Host.Host))).AllowAnonymous();

        app.MapGet("/api/og/{**path}", (
            [FromServices] IMessageHub hub, [FromServices] OgCardRenderer renderer, HttpContext http, string path,
            CancellationToken ct) =>
        {
            var nodePath = (path ?? "").Trim('/');
            if (nodePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                nodePath = nodePath[..^4];
            if (nodePath.Length == 0)
                return Task.FromResult(PngResult(http, renderer.RenderSite(http.Request.Host.Host)));

            return SeoResolver.ResolveShareableNode(hub, nodePath)
                .Select(shareable => shareable is not { } cleared
                    ? Results.NotFound()
                    : CardResult(http, renderer, cleared.Node, cleared.AnonymousReadable))
                .Catch<IResult, Exception>(_ => Observable.Return(Results.NotFound()))
                .FirstAsync()
                .ObserveCompletion(LateFault(hub, $"/api/og/{nodePath}"), ct)!;
        }).AllowAnonymous();
    }

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
    /// <see cref="SeoResolver.ResolveShareableNode"/> — the same one predicate — so a mark reaches
    /// this route only for a node the fail-closed <see cref="AnonymousGate"/> admits or one whose
    /// scope opted in to <see cref="PartitionAccessPolicy.PublicPreview"/>; a missing node, a
    /// withheld one that did not opt in, and a node with no mark all answer the same 404. There is no
    /// parallel permission rule here to drift from the page's.</para>
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
            return SeoResolver.ResolveShareableNode(hub, nodePath)
                .Select(shareable => shareable is not { } cleared
                    ? Results.NotFound()
                    : IconResult(http, cleared.Node, pixels, unrenderable, cleared.AnonymousReadable))
                .Catch<IResult, Exception>(_ => Observable.Return(Results.NotFound()))
                .FirstAsync()
                .ObserveCompletion(LateFault(hub, $"/api/icon/{nodePath}"), ct)!;
        }).AllowAnonymous();

    /// <summary>
    /// One node's rasterized mark, or 404 when it carries none this can draw. Internal so the
    /// endpoint's own decision — not a re-implementation of it — is what the tests exercise.
    /// </summary>
    /// <param name="http">The request, for conditional-GET and response headers.</param>
    /// <param name="node">The node, already cleared by the caller — either anonymous-readable or
    /// preview-opted-in.</param>
    /// <param name="size">The square edge in pixels.</param>
    /// <param name="onUnrenderable">Sink for markup that is present but cannot be drawn — an
    /// AUTHORED icon that fails to parse is a content defect worth a line in the log, not something
    /// to swallow into an indistinguishable 404.</param>
    internal static IResult IconResult(
        HttpContext http, MeshNode node, int size, Action<Exception>? onUnrenderable = null,
        bool sharedCacheable = true)
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
        // Cacheable exactly as the share card is, and for the same reasons on both legs — see
        // CacheDirective: shared for a node the gate admitted, private/no-store for one cleared only
        // by the revocable preview opt-in.
        http.Response.Headers.CacheControl = CacheDirective(sharedCacheable);
        return Results.File(png, "image/png");
    }

    private static IResult CardResult(
        HttpContext http, OgCardRenderer renderer, MeshNode node, bool sharedCacheable) =>
        PngResult(http, renderer.Render(CardContent(node)), sharedCacheable);

    /// <summary>
    /// Everything the card says about a node, read off the node the resolver already cleared:
    /// name, description (with the catalog-copy fallbacks), category or type as the eyebrow, its
    /// own mark through the SAME backplate policy the favicon route draws
    /// (<see cref="SeoResolver.ResolveIconSvg"/>), the price when it sells something, and the
    /// path. Internal so a test reads the endpoint's own mapping rather than re-deriving it.
    ///
    /// <para>🚨 Takes the NODE, not a <see cref="SeoPageData"/>. It never needed more than the node
    /// — the description it drew was always <see cref="SeoResolver.ExtractDescription"/> of it — and
    /// taking the node means the card path cannot reach <see cref="SeoPageData.Body"/> even by
    /// accident. That matters since the route now also serves a node the gate REFUSED, on a scope
    /// that opted in to <see cref="PartitionAccessPolicy.PublicPreview"/>: the picture discloses the
    /// same name, summary, eyebrow and mark the head does, and nothing else.</para>
    /// </summary>
    internal static OgCardContent CardContent(MeshNode node)
    {
        var price = SeoResolver.ContentDecimal(node, "price");
        return new OgCardContent
        {
            Title = node.Name ?? node.Id,
            Description = SeoResolver.ExtractDescription(node),
            Eyebrow = string.IsNullOrWhiteSpace(node.Category) ? TypeLeaf(node.NodeType) : node.Category,
            IconSvg = SeoResolver.ResolveIconSvg(node),
            Price = price is > 0m
                ? $"{SeoResolver.ContentString(node, "currency") ?? "CHF"} {price.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}"
                : null,
            Path = node.Path,
            AccentSeed = node.Path,
        };
    }

    /// <summary>The last segment of a node type — <c>Store/Plugin</c> reads as "Plugin" on the card.</summary>
    private static string? TypeLeaf(string? nodeType) =>
        string.IsNullOrWhiteSpace(nodeType) ? null : nodeType[(nodeType.LastIndexOf('/') + 1)..];

    private static IResult PngResult(HttpContext http, byte[] png, bool sharedCacheable = true)
    {
        var etag = $"\"{Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(png))}\"";
        if (string.Equals(http.Request.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = CacheDirective(sharedCacheable);
        return Results.File(png, "image/png");
    }

    /// <summary>
    /// 🚨 THE CACHE DIRECTIVE FOLLOWS WHICH DECISION CLEARED THE RESPONSE, and that is a correctness
    /// property rather than a tuning one.
    ///
    /// <para><b>Gate-admitted ⇒ shared-cacheable.</b> Everything drawn is already served to anonymous
    /// callers on the page itself, crawlers refetch cards aggressively, and the strong ETag is the
    /// render's own hash — so a node that changes its mark produces a new picture rather than a stale
    /// one.</para>
    ///
    /// <para><b>Preview-only ⇒ <c>private, no-store</c>.</b> That response is reachable because a
    /// POLICY says so, and a policy is revocable while a shared cache never re-asks the origin: a day
    /// of <c>public, max-age</c> would leave a withdrawn disclosure publicly retrievable after the
    /// owner withdrew it. The ETag still goes out, so a conditional GET works for whoever holds
    /// one.</para>
    ///
    /// <para>🚨 It does NOT reach the unfurler's own copy — Slack, Teams, iMessage and LinkedIn keep a
    /// preview for hours to days and no response header controls that. Revocation is immediate at the
    /// origin and eventually-consistent at the consumer; <c>Doc/Architecture/LinkPreviews</c> says so
    /// where an owner reads about the flag.</para>
    /// </summary>
    /// <param name="sharedCacheable">True when the anonymous gate admitted the node.</param>
    private static string CacheDirective(bool sharedCacheable) =>
        sharedCacheable ? "public, max-age=86400" : "private, no-store";

    /// <summary>
    /// The sitemap XML, built reactively: candidate roots from the (System-read) type queries,
    /// each gated through the REAL anonymous permission check, then every page-shaped descendant
    /// of an admitted root, each gated the same way. Cold.
    ///
    /// <para>Fail-open to fewer URLs — never to ZERO of them. It faults with
    /// <see cref="SitemapUndecidedException"/> rather than render a urlset that declares no public
    /// root it never checked; the route maps that to 503.</para>
    /// </summary>
    public static IObservable<string> BuildSitemap(IMessageHub hub, string baseUrl) =>
        EnumeratePublished(hub)
            .Select(surface => surface.AssertsWhatItDidNotCheck
                ? throw new SitemapUndecidedException(surface.Undecided!)
                : Render(baseUrl, surface.Pages.Select(p => (p.Node, p.Path)).ToList()));

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
    public static IObservable<PublishedSurface> EnumeratePublished(IMessageHub hub)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        // DECIDED, not undecided: a host with no mesh publishes nothing, permanently and by
        // configuration — the same reasoning AnonymousGate applies to a mesh with no permission
        // evaluator. Retrying changes nothing, so calling it "unavailable" would be its own lie.
        if (mesh is null)
            return Observable.Return(PublishedSurface.Decided([]));

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
                mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(RootCandidateQuery(type)))
                    .Take(1)
                    .Select(change => change.Items.ToList())))
            .ToObservable().Concat().ToList()
            .Select(lists => lists.SelectMany(l => l).DistinctBy(n => n.Path).ToList());

        return candidates
            .SelectMany(roots => roots.Count == 0
                ? Observable.Return(new List<RootAdmission>())
                : roots.Select(Admit).ToObservable().Concat().ToList())
            // PagesOf yields UNNAMED (MeshNode, string) tuples, so address them positionally.
            .Select(admissions => new PublishedSurface(
                admissions
                    .SelectMany(a => a.Pages)
                    .DistinctBy(p => p.Item2)
                    .Select(p => new PublishedPage(p.Item1, p.Item2))
                    .ToList(),
                // FIRST reason, not all of them: one sentence is what a log line and a 503 need,
                // and an undecided fold is a property of the deployment rather than of the root
                // that happened to be asked first.
                admissions.Select(a => a.Undecided).FirstOrDefault(reason => reason is not null)))
            // 🚨 The bound STAYS and the swallow under it GOES. A 20 s cap at an HTTP edge is
            // legitimate; what was not is that timing out and completing a census produced the
            // same value. It now faults, and SitemapUnavailable names it.
            .Timeout(TimeSpan.FromSeconds(20));

        // 🚨 The TRI-state, not the bool. AnonymousGate.AllowAnonymous is documented as usable
        // "only where 'unknown' and 'not public' lead to the SAME correct action and nothing is
        // asserted" — and it even names omitting a page from the sitemap as such a place. That is
        // right per PAGE (PagesOf still uses it, and always emits its root regardless) and wrong
        // per ROOT, because the roots ARE the sitemap: omit every one of them and the document
        // that comes out is an assertion. #4751.
        IObservable<RootAdmission> Admit(MeshNode root) =>
            AnonymousGate.Evaluate(hub, root.Path)
                .Take(1)
                .SelectMany(outcome => outcome.IsGranted
                    ? PagesOf(mesh, accessService, hub, root)
                        .Select(pages => new RootAdmission(pages, null))
                    : Observable.Return(new RootAdmission(
                        [],
                        // A DENIAL is decided — the root is simply not public, and leaving it out
                        // is the whole point of the gate. Only "no verdict" is carried out.
                        outcome.IsUndetermined
                            ? $"the anonymous gate on '{root.Path}' reached no verdict: "
                              + outcome.UndeterminedReason
                            : null)));
    }

    /// <summary>
    /// What one candidate root contributed: the pages it published, and — when its publicness
    /// could not be decided — why. Both are needed: a root can contribute no page because it is
    /// private (decided, ordinary) or because nothing could establish either way (#4751).
    /// </summary>
    /// <param name="Pages">The pages admitted under this root; empty when it is not public.</param>
    /// <param name="Undecided">Null unless the gate reached no verdict on this root.</param>
    private sealed record RootAdmission(IReadOnlyList<(MeshNode, string)> Pages, string? Undecided);

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
            // Bounded degradation, unlike the sinks this change removed: the root itself is still
            // published, so this can cost pages and can never produce the zero-root document of
            // #4751. It was SILENT though, which is the other half of that issue — a sitemap that
            // quietly lost a course's chapters left nothing to read.
            .Catch<IReadOnlyList<(MeshNode, string)>, Exception>(ex =>
            {
                hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(SeoEndpoints))
                    .LogWarning(
                        ex,
                        "sitemap: listing the pages below '{Root}' failed; publishing the root "
                        + "alone and none of its pages",
                        root.Path);
                return Observable.Return<IReadOnlyList<(MeshNode, string)>>([self]);
            });
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
