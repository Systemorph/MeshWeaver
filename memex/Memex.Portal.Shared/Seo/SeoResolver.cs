using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Seo;

/// <summary>
/// A page's OWN icon, ready to hang off a <c>&lt;link rel="icon"&gt;</c> in its head.
/// </summary>
/// <param name="Href">The icon as something an <c>href</c> can carry: a URL the portal already
/// serves (a content-collection file, a shipped glyph), or an inline <c>data:</c> URI for an icon
/// whose value is MARKUP rather than a location (an inline <c>&lt;svg&gt;</c>, an emoji).</param>
/// <param name="Type">The media type to declare, or null when the value alone does not pin one
/// down — declaring the WRONG type is worse than declaring none, and a consumer that ranks icons
/// by type would then rank this one on a lie.</param>
public sealed record PageIcon(string Href, string? Type)
{
    /// <summary>
    /// The link relation — <c>icon</c> for the tab strip, <c>apple-touch-icon</c> for Safari's
    /// bookmark / Add-to-Dock tile, which is a SEPARATE channel and never falls back to the
    /// favicon.
    ///
    /// <para>🚨 An <c>init</c> PROPERTY, deliberately not a third primary-constructor parameter.
    /// A record's primary constructor is a BINARY contract with every module already compiled
    /// against it — adding a parameter replaces the signature even when it carries a default, and
    /// the host aborts at boot on the mismatch in both directions. A new property is additive, so
    /// this cannot split an image; <c>scripts/check-record-signatures.py</c> is what says so.</para>
    /// </summary>
    public string Rel { get; init; } = "icon";

    /// <summary>The <c>sizes</c> attribute, for a raster icon that has exactly one pixel size. Null
    /// for a scalable (SVG) icon, where declaring a size would tell a browser that ranks icons by
    /// size something untrue. Same <c>init</c> reasoning as <see cref="Rel"/>.</summary>
    public string? Sizes { get; init; }
}

/// <summary>
/// What the crawler-facing head needs to know about the requested page: the resolved node and
/// the pieces of its content the meta tags are built from. Only ever produced for pages an
/// ANONYMOUS visitor may read (the <see cref="AnonymousGate"/> decision) — a private node's
/// name/description must never leak into markup served to a logged-out crawler.
/// </summary>
public sealed record SeoPageData(MeshNode Node, string? Description, string? Image)
{
    /// <summary>The node's pre-rendered markdown body, when it carries one — served inside
    /// <c>&lt;noscript&gt;</c> so non-JS crawlers index the actual page content.</summary>
    public string? PreRenderedHtml => Node.PreRenderedHtml;
}

/// <summary>
/// Server-side SEO resolution for the initial HTTP response. Reactive end to end; the ONE
/// <c>Task</c> bridge sits at the Razor static-SSR boundary (<see cref="ResolveAsync"/>), the
/// same adapter shape the MCP/REST surfaces use. Fail-open to null: a slow or faulted mesh
/// never delays page delivery — the page just ships the generic head.
/// </summary>
public static class SeoResolver
{
    /// <summary>Per-request stash key so the head and body components resolve ONCE.</summary>
    public const string HttpContextItem = "Memex.Seo.PageData";

    /// <summary>Route prefixes that are never mesh nodes — skipped without touching the mesh.
    /// "mcp" is deliberately NOT here: <c>Mcp</c> is a real partition (the MCP Server store
    /// plugin) whose cover renders at <c>/Mcp</c>, so it resolves like any node page; MCP
    /// protocol traffic never reaches the page render path this resolver serves (the endpoint
    /// route and <c>NonfileRouteConstraint</c> keep it off).</summary>
    private static readonly string[] NonNodePrefixes =
        ["login", "api", "_blazor", "_framework", "_content", "dev", "static", "webhooks"];

    /// <summary>Whether the request path can be a node page worth resolving.</summary>
    public static bool IsCandidatePath(string? path)
    {
        var trimmed = (path ?? "").Trim('/');
        if (trimmed.Length == 0)
            return false;
        var first = trimmed.Split('/')[0];
        return !NonNodePrefixes.Any(p => string.Equals(p, first, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the request path to its node and gates it through
    /// <see cref="AnonymousGate.AllowAnonymous"/>. Emits null when the path is no node, the node
    /// is not anonymous-readable, or anything errors/times out. Cold.
    /// </summary>
    public static IObservable<SeoPageData?> Resolve(IMessageHub hub, string path)
    {
        var resolver = hub.ServiceProvider.GetService<IPathResolver>();
        if (resolver is null)
            return Observable.Return<SeoPageData?>(null);
        return resolver.ResolvePath(path.Trim('/'))
            .Take(1)
            .SelectMany(resolution => resolution?.Node is not { } node
                ? Observable.Return<SeoPageData?>(null)
                // The BOOLEAN projection is correct here and stays (#2901): "not public" and "the
                // gate could not find out" both mean WITHHOLD the rich metadata, and neither is
                // asserted to a human — an omitted og: block is not a claim about the visitor. A
                // caller whose answer becomes a redirect, a status code or a message must use
                // AnonymousGate.Evaluate and branch on IsUndetermined first.
                : AnonymousGate.AllowAnonymous(hub, resolution.Prefix)
                    .Take(1)
                    .Select(allowed => allowed
                        ? new SeoPageData(node, ExtractDescription(node), ShareImage(node))
                        : null))
            .Timeout(TimeSpan.FromSeconds(3))
            .Catch<SeoPageData?, Exception>(_ => Observable.Return<SeoPageData?>(null));
    }

    /// <summary>
    /// The static-SSR boundary bridge — the only <c>Task</c> on this surface, and it goes through
    /// <see cref="ReactiveCompletion.ObserveCompletion{T}(System.IObservable{T}, System.Action{System.Exception}, System.Threading.CancellationToken)"/> rather than Rx's <c>.ToTask()</c>:
    /// the latter resumes its awaiter INLINE on the signalling thread, which here is whichever mesh
    /// hub answered the path resolution — so the rest of the Razor render would run on that hub's
    /// action block (forbidden since 2026-08-30).
    /// </summary>
    public static Task<SeoPageData?> ResolveAsync(IMessageHub hub, string path)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SeoResolver));
        // Resolve()'s trailing Catch turns every FAULT into a value, so on the fault path it emits
        // exactly once. FirstAsync is kept because the remaining case it does NOT cover is an
        // upstream that completes EMPTY: FirstAsync raises there, and ObserveCompletion would
        // otherwise settle with a null that reads as "no SEO data" — the same answer a private node
        // gets. Behaviour is unchanged from the .ToTask() this replaced, which raised identically.
        return Resolve(hub, path)
            .FirstAsync()
            .ObserveCompletion(ex => logger?.LogWarning(
                ex, "SEO resolution for '{Path}' faulted after the head had already been produced", path));
    }

    /// <summary>
    /// The page description for meta tags: the node's Description, else the content's
    /// <c>abstract</c>/<c>description</c> member (untyped — content arrives as JSON here).
    /// </summary>
    public static string? ExtractDescription(MeshNode node) =>
        FirstNonEmpty(
            node.Description,
            ContentString(node, "abstract"),
            ContentString(node, "description"));

    /// <summary>
    /// The AUTHORED share image, or null when the node carries none (the caller then falls back to
    /// the generated card — see <see cref="ShareImage"/>).
    ///
    /// <para>🚨 <c>ogImage</c> is listed FIRST because it is the field store plugins actually
    /// declare. This read used to check only <c>poster</c> and <c>thumbnail</c>, so every plugin's
    /// hand-made <c>og.png</c> was ignored and no store page has ever emitted an <c>og:image</c> —
    /// the tag is written only when this returns non-null. <c>poster</c> and <c>thumbnail</c>
    /// remain for markdown pages and video nodes.</para>
    ///
    /// <para>Root-relative or absolute URLs only: a bare filename would resolve against whatever
    /// path the crawler happened to fetch.</para>
    /// </summary>
    public static string? ExtractImage(MeshNode node)
    {
        var candidate = FirstNonEmpty(
            ContentString(node, "ogImage"),
            ContentString(node, "poster"),
            ContentString(node, "thumbnail"));
        return candidate is not null
            && (candidate.StartsWith('/') || candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            ? candidate
            : null;
    }

    /// <summary>
    /// The image a public page shares with: whatever it authored, else the card the portal draws
    /// for it. Never null — "this page has an Open Graph card" is the default, not an opt-in.
    /// </summary>
    public static string ShareImage(MeshNode node) =>
        ExtractImage(node) ?? $"/api/og/{node.Path}";

    /// <summary>The one media type that tells every consumer "this icon scales losslessly".</summary>
    private const string SvgMediaType = MeshNodeImageHelper.SvgMediaType;

    /// <summary>
    /// 🚨 THE PAGE'S OWN ICON — the node's icon, so a node page identifies itself rather than the
    /// portal it happens to live in.
    ///
    /// <para>Every page of a Blazor portal serves ONE site-wide <c>&lt;link rel="icon"&gt;</c> (see
    /// <c>App.razor</c>), so every link preview of every node — our own <c>OgCard</c>, and equally a
    /// Slack / Teams / LinkedIn unfurl — drew the same MeshWeaver logo. Yet each node already
    /// carries a distinctive icon, which the portal renders everywhere INSIDE the app. This lifts
    /// that same icon into the head, where the standards-based icon channel is, so every consumer
    /// gets it for free without knowing anything about MeshWeaver.</para>
    ///
    /// <para>🚨 It is the icon ON THE NODE — <see cref="MeshNode.Icon"/> — and nothing else. No
    /// synthesised badge, no letter tile, no NodeType stand-in: those would put a picture in the
    /// head that the node never chose and that the app never renders for it. A node that carries no
    /// icon of its own simply keeps the portal favicon, which is the honest answer for a page with
    /// no mark of its own.</para>
    ///
    /// <para>The value is resolved exactly the way the in-app icon is
    /// (<see cref="MeshNodeImageHelper.ResolveContentPath"/>) — a <c>content:</c> reference becomes
    /// the access-controlled content URL, an inline <c>&lt;svg&gt;</c> stays that same svg, a URL
    /// stays that URL — so the tab, the card and the app can never disagree about what a node looks
    /// like.</para>
    /// </summary>
    /// <param name="node">The node whose page is being served.</param>
    /// <returns>Its own icon, or null when it carries none an <c>href</c> can point at.</returns>
    public static PageIcon? ResolveIcon(MeshNode node)
    {
        // An inline <svg> is MARKUP, not a location, so it travels as a data URI — the same
        // mechanism App.razor already uses for the per-instance favicon. The svg itself is
        // untouched: it is the node's icon, byte for byte.
        if (ResolveIconSvg(node) is { } svg)
            return new PageIcon(SvgDataUri(svg), SvgMediaType);

        var icon = MeshNodeImageHelper.ResolveContentPath(node.Icon, node.Path);
        if (string.IsNullOrWhiteSpace(icon))
            return null;

        // A URL or data URI the node carries — used as written.
        if (MeshNodeImageHelper.IsImageUrl(icon))
            return new PageIcon(icon, MediaTypeOf(icon));

        // Anything else (an emoji, a bare word) is a character, not a picture, and no href can
        // carry it. The portal favicon stays rather than inventing a graphic for it.
        return null;
    }

    /// <summary>
    /// The node's own icon AS SVG SOURCE, or null when it carries none that is markup.
    ///
    /// <para>This is the exact value <see cref="ResolveIcon"/> percent-encodes into its
    /// <c>data:image/svg+xml</c> href — one resolution, two consumers, so the
    /// <see cref="PageIcon"/> in the head and the PNG the rasterizer serves for the SAME page can
    /// never be pictures of different things.</para>
    ///
    /// <para>Only INLINE markup qualifies. An icon that is a URL (a content-collection file, a
    /// shipped glyph) is a location this process would have to fetch — over its own
    /// access-controlled route, from an anonymous request — to rasterize; a raster URL needs no
    /// rasterizing at all, because Safari reads those already.</para>
    /// </summary>
    /// <param name="node">The node whose page is being served.</param>
    public static string? ResolveIconSvg(MeshNode node)
    {
        var icon = MeshNodeImageHelper.ResolveContentPath(node.Icon, node.Path);
        return MeshNodeImageHelper.IsInlineSvg(icon) ? icon : null;
    }

    /// <summary>The route the rasterized favicon of one node is served from.</summary>
    /// <param name="nodePath">The node's mesh path, which is also its public URL path.</param>
    /// <param name="size">The square edge in pixels.</param>
    public static string RasterIconUrl(string nodePath, int size) =>
        $"/api/icon/{nodePath}.png?size={size}";

    /// <summary>
    /// 🚨 EVERY icon link the page's head should carry, in declaration order.
    ///
    /// <para><b>Why more than one.</b> Safari renders no SVG favicon — not from a data URI, not
    /// from a URL — so a page whose only icon link is the node's <c>&lt;svg&gt;</c> mark wore the
    /// portal favicon on every Mac and iPhone, in and out of circuit (issue #2075, item 3). The
    /// standards-based answer is not to give up the scalable icon: declare BOTH and let each
    /// browser take the one it can read. A browser that understands SVG prefers it (scalable beats
    /// a fixed 32 px on a retina tab strip); Safari skips it and takes the PNG.</para>
    ///
    /// <para><b>Why <c>apple-touch-icon</c> is separate.</b> It is a different channel with a
    /// different job — the large bookmark / Start-Page / Add-to-Dock tile — and Safari does NOT
    /// fall back to the favicon for it: with none declared it draws its own letter tile. So a node
    /// with a mark needs one pointed at that mark, or its tile says nothing about it.</para>
    ///
    /// <para>Nothing is synthesised, exactly as <see cref="ResolveIcon"/> promises: a node with no
    /// icon of its own yields NO links at all, and a node whose icon is already a raster image
    /// yields the single link it always did — Safari reads those without help.</para>
    /// </summary>
    /// <param name="node">The node whose page is being served.</param>
    /// <returns>The links to declare; empty when the node has no icon an <c>href</c> can carry.</returns>
    public static IReadOnlyList<PageIcon> ResolveIconLinks(MeshNode node)
    {
        if (ResolveIcon(node) is not { } icon)
            return [];
        if (ResolveIconSvg(node) is null)
            return [icon];
        return
        [
            icon,
            new PageIcon(RasterIconUrl(node.Path, IconRasterizer.FaviconSize), "image/png")
            {
                Sizes = $"{IconRasterizer.FaviconSize}x{IconRasterizer.FaviconSize}",
            },
            new PageIcon(RasterIconUrl(node.Path, IconRasterizer.AppleTouchSize), "image/png")
            {
                Rel = "apple-touch-icon",
                Sizes = $"{IconRasterizer.AppleTouchSize}x{IconRasterizer.AppleTouchSize}",
            },
        ];
    }

    /// <summary>The media type an icon URL pins down by itself, or null when it does not — a
    /// consumer ranking icons by type must never be told a wrong one.</summary>
    private static string? MediaTypeOf(string icon) =>
        icon.StartsWith("data:" + SvgMediaType, StringComparison.OrdinalIgnoreCase)
        || (!icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            && icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            ? SvgMediaType
            : null;

    // One encoding rule for the whole product: the head, the tab and the app all carry an inline
    // <svg> icon the same way (MeshNodeImageHelper.SvgDataUri).
    private static string SvgDataUri(string svg) => MeshNodeImageHelper.SvgDataUri(svg);

    /// <summary>
    /// A string member of the node's content, by camelCase JSON name. Content arrives here in two
    /// shapes and both must resolve the same way: untyped <see cref="JsonElement"/> (node-native
    /// types the portal hub hasn't registered), or a TYPED record when the hub knows the CLR type —
    /// a markdown page resolves as <c>MarkdownContent</c>, so reading only the JsonElement shape
    /// silently dropped <c>og:image</c> for every markdown node's <c>thumbnail</c>.
    /// </summary>
    public static string? ContentString(MeshNode node, string property) =>
        node.Content switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } je
                when je.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String => value.GetString(),
            JsonElement or null => null,
            var typed => TypedMember(typed, property) as string,
        };

    /// <summary>A decimal member of the node's content, by camelCase JSON name (both shapes).</summary>
    public static decimal? ContentDecimal(MeshNode node, string property) =>
        node.Content switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } je
                when je.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.Number => value.GetDecimal(),
            JsonElement or null => null,
            var typed => TypedMember(typed, property) is decimal d ? d : null,
        };

    /// <summary>The PascalCase CLR property matching a camelCase JSON member name.</summary>
    private static object? TypedMember(object content, string jsonName) =>
        content.GetType()
            .GetProperty(char.ToUpperInvariant(jsonName[0]) + jsonName[1..])?
            .GetValue(content);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
