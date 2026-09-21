using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
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
    /// <summary>The node's pre-rendered markdown body, when the node CARRIES one. Prefer
    /// <see cref="Body"/>: this is null for every node whose markdown was never mirrored onto the
    /// node (a plugin cover's <c>body</c>, a node read through a projection that drops the mirror),
    /// and a crawler served nothing for exactly those pages (#4056).</summary>
    public string? PreRenderedHtml => Node.PreRenderedHtml;

    /// <summary>
    /// The part of the request path BEYOND the resolved node — a layout-area route
    /// (<c>/{node}/{area}/{id}</c>) or a path that does not exist and fell back to its nearest
    /// ancestor. Null when the URL named the node exactly. The head reads it to keep a fallback
    /// page out of the index: the framework answers such a URL with the ancestor and HTTP 200,
    /// which a search engine otherwise files as a soft 404 against the ancestor.
    ///
    /// <para><c>init</c> property, not a primary-constructor parameter — the record's constructor is
    /// a binary contract with every module compiled against it (see <see cref="PageIcon.Rel"/>).</para>
    /// </summary>
    public string? Remainder { get; init; }

    /// <summary>
    /// 🚨 THE PAGE BODY A CRAWLER READS — the node's content as HTML, rendered on the server so the
    /// FIRST HTTP response carries it. A Blazor Server page otherwise ships an empty
    /// <c>&lt;body&gt;</c> and fills it over the circuit, and a crawler does not hold a circuit:
    /// measured 2026-09-11 on memex.meshweaver.cloud, every public page — documentation, course
    /// lessons, plugin covers — arrived at Googlebot with a rich head and no text at all, and the
    /// host had zero pages in Google's index.
    ///
    /// <para>Resolution order: the prerendered HTML the node already carries; else the
    /// <c>prerenderedHtml</c> member of its content; else its markdown (<c>content</c> for a
    /// markdown node, <c>body</c> for a plugin cover) rendered now. Null when the node has no
    /// document-shaped content (a data node, a pure layout-area page). Only ever produced for a
    /// node the <see cref="AnonymousGate"/> admitted, like every other member here — a gated
    /// chapter is refused before this is computed.</para>
    /// </summary>
    public string? Body => PreRenderedHtml is { Length: > 0 } mirrored ? mirrored : SeoResolver.RenderBody(Node);
}

/// <summary>
/// 🚨 THE CARD A GATED PAGE SHARES WITH — composed from the nearest ancestor the
/// <see cref="AnonymousGate"/> ADMITS, plus the request path's own segments. Never from the page
/// that was withheld.
///
/// <para><b>What it fixes.</b> A link into a private subtree of a PUBLIC root unfurled as the bare
/// site card. Measured 2026-09-20 on <c>www.meshweaver.cloud</c>: <c>/PG3Reporting</c> carried
/// <c>og:title "Fund Reporting"</c>, a description and <c>/api/og/PG3Reporting.png</c>, while every
/// descendant — <c>…/Funds</c>, <c>…/Funds/InsuranceCore</c>,
/// <c>…/Funds/InsuranceCore/2026-06-30</c> — carried <c>og:title "MeshWeaver"</c> and
/// <c>/api/og.png</c>. Not depth (<c>/Doc/Architecture/AccessControl</c> unfurls fully on the same
/// host) but ACCESS: that partition's <c>_Policy</c> declares a <c>RedirectOnDenied</c> and no
/// <c>PublicRead</c>, so its root is a public listing over gated content.
/// </para>
///
/// <para>🚨 <b>Why this discloses nothing.</b> The tail of <see cref="Title"/> is the request path's
/// own segments — they are in the URL the sharer pasted into the chat, so rendering them back tells
/// the reader nothing they are not already looking at. Everything else belongs to the PUBLIC
/// ancestor and is already served to anyone who asks: its name, its description, and the card at
/// <c>/api/og/{ancestor}.png</c> that the same gate already serves anonymously. The withheld node's
/// own <see cref="MeshNode.Name"/>, description, icon and content are never read — see
/// <see cref="SeoResolver.ComposeAncestorCard"/>, which cannot read them because it is never given
/// them.
/// </para>
///
/// <para>When no ancestor is public either there is no card here, and the caller keeps the site
/// card. That is the honest floor: a private page under a private root says only what its URL
/// already said.</para>
/// </summary>
/// <param name="Title">The ancestor's title, then the requested path's segments below it.</param>
/// <param name="Description">The ancestor's description, plus the partition's call to action when it
/// declares one. Null when the ancestor has neither.</param>
/// <param name="Image">The ancestor's share image — root-relative or absolute, exactly as
/// <see cref="SeoResolver.ShareImage"/> returns it, so the caller prefixes the host and declares its
/// size the same way it does for a public page.</param>
/// <param name="AncestorPath">The node the card was built from. Not rendered; it is what a log line
/// and a test name to say WHICH ancestor answered.</param>
public sealed record SeoAncestorCard(
    string Title, string? Description, string Image, string AncestorPath);

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

    /// <summary>
    /// How long any one of these resolutions may take before the page ships without it. Named
    /// rather than repeated, so the public-page pass and the public-ancestor pass of the SAME HTTP
    /// response cannot drift into different budgets.
    /// </summary>
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(3);

    /// <summary>Route prefixes that are never mesh nodes — skipped without touching the mesh.
    /// "mcp" is deliberately NOT here: <c>Mcp</c> is a real partition (the MCP Server store
    /// plugin) whose cover renders at <c>/Mcp</c>, so it resolves like any node page; MCP
    /// protocol traffic never reaches the page render path this resolver serves (the endpoint
    /// route and <c>NonfileRouteConstraint</c> keep it off).</summary>
    private static readonly ImmutableHashSet<string> NonNodePrefixes = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "login", "api", "_blazor", "_framework", "_content", "dev", "static", "webhooks");

    /// <summary>Whether the request path can be a node page worth resolving.</summary>
    public static bool IsCandidatePath(string? path)
    {
        var trimmed = (path ?? "").Trim('/');
        if (trimmed.Length == 0)
            return false;
        var first = trimmed.Split('/')[0];
        return !NonNodePrefixes.Contains(first);
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
                        {
                            Remainder = string.IsNullOrEmpty(resolution.Remainder)
                                ? null
                                : resolution.Remainder,
                        }
                        : null))
            .Timeout(ResolveBudget)
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
    /// 🚨 THE CALL-TO-ACTION KEY. The one sentence this surface adds to a gated page's card. It is
    /// platform-owned text, so it follows the VIEWER's language and lives in the catalog
    /// (<c>strings.{en,de}.json</c>) like every other string a human reads. Public so the head, the
    /// tests and a translator can all name the same key.
    /// </summary>
    public const string CallToActionKey = "seo.gatedCard.accessRoute";

    /// <summary>
    /// How far above the requested path a public ancestor is looked for. The walk is over the
    /// ancestors of the deepest node that EXISTS (<see cref="AddressResolution.Prefix"/>), never
    /// over the raw URL segments, so a 40-segment URL into nothing costs one resolution and stops —
    /// this bound only ever bites content nested deeper than any in the fleet, and it is what keeps
    /// an anonymous request from buying an unbounded number of permission folds.
    /// </summary>
    private const int MaxAncestorsWalked = 12;

    /// <summary>
    /// 🚨 THE FALLBACK for a page the gate WITHHELD: the nearest anonymous-readable ancestor's card,
    /// captioned with the requested path. Emits null — meaning "keep the site card" — when the URL
    /// matches no node at all, when no ancestor is public either, or when anything errors or times
    /// out. Cold.
    ///
    /// <para>Call it only when <see cref="Resolve"/> answered null: this walk starts STRICTLY ABOVE
    /// the node the URL resolved to, so it can never re-serve a page that was withheld.</para>
    /// </summary>
    /// <param name="hub">The hub whose path resolver and permission evaluator answer.</param>
    /// <param name="path">The node path the visitor asked for.</param>
    /// <param name="locale">The VIEWER's language tag, read explicitly off their AccessContext by
    /// the caller — never from an ambient culture. Null ⇒ English.</param>
    public static IObservable<SeoAncestorCard?> ResolvePublicAncestor(
        IMessageHub hub, string path, string? locale = null)
    {
        var resolver = hub.ServiceProvider.GetService<IPathResolver>();
        var requested = (path ?? "").Trim('/');
        if (resolver is null || requested.Length == 0)
            return Observable.Return<SeoAncestorCard?>(null);

        return resolver.ResolvePath(requested)
            .Take(1)
            // 🚨 The walk starts at the deepest node that EXISTS, not at the URL's last segment.
            // ResolvePath already falls back to the nearest existing ancestor — that is how
            // `/PG3Reporting/Subscribe`, a layout-area route, resolves to `PG3Reporting` and unfurls
            // as it today — so a resolution of null means NO node matches any prefix of this URL.
            // There is nothing above it to find, and the honest answer is the site card.
            .SelectMany(resolution => resolution is null
                ? Observable.Return<SeoAncestorCard?>(null)
                : NearestPublicAncestor(hub, AncestorPaths(resolution.Prefix))
                    .SelectMany(ancestor => ancestor is null
                        ? Observable.Return<SeoAncestorCard?>(null)
                        : CallToAction(hub, resolution.Prefix, locale)
                            .Select(callToAction =>
                                ComposeAncestorCard(ancestor, requested, callToAction))))
            // Same budget and the same fail-open-to-null as Resolve: a slow or faulted mesh costs
            // the card, never the page, and the caller's site card is the floor.
            .Timeout(ResolveBudget)
            .Catch<SeoAncestorCard?, Exception>(_ => Observable.Return<SeoAncestorCard?>(null));
    }

    /// <summary>
    /// The static-SSR boundary bridge for <see cref="ResolvePublicAncestor"/> — same shape and same
    /// reasoning as <see cref="ResolveAsync"/>: <c>ObserveCompletion</c>, never <c>.ToTask()</c>,
    /// because the latter would resume the rest of the Razor render inline on whichever mesh hub
    /// answered.
    /// </summary>
    /// <param name="hub">The hub whose path resolver and permission evaluator answer.</param>
    /// <param name="path">The node path the visitor asked for.</param>
    /// <param name="locale">The viewer's language tag; null ⇒ English.</param>
    public static Task<SeoAncestorCard?> ResolvePublicAncestorAsync(
        IMessageHub hub, string path, string? locale = null)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SeoResolver));
        return ResolvePublicAncestor(hub, path, locale)
            .FirstAsync()
            .ObserveCompletion(ex => logger?.LogWarning(
                ex,
                "The public-ancestor card for '{Path}' faulted after the head had already been produced",
                path));
    }

    /// <summary>
    /// 🚨 THE COMPOSITION — and the whole disclosure argument in one signature: it takes a
    /// <see cref="SeoPageData"/> the gate ADMITTED plus the requested PATH, and there is
    /// deliberately no overload that takes the withheld node. Pure — no hub, no IO — so everything
    /// the card can possibly say is decided here, and is testable without a mesh.
    /// </summary>
    /// <param name="ancestor">The nearest anonymous-readable ancestor's page data.</param>
    /// <param name="requestedPath">The node path the visitor asked for.</param>
    /// <param name="callToAction">The localized sentence to append, or null for none.</param>
    public static SeoAncestorCard ComposeAncestorCard(
        SeoPageData ancestor, string requestedPath, string? callToAction)
    {
        ArgumentNullException.ThrowIfNull(ancestor);
        var tail = TailBelow(requestedPath, ancestor.Node.Path);
        var ancestorTitle = ancestor.Node.Name ?? ancestor.Node.Id;
        var description = FirstNonEmpty(ancestor.Description) is { } text
            ? callToAction is null ? text : $"{text} {callToAction}"
            : callToAction;
        return new SeoAncestorCard(
            tail.Length == 0 ? ancestorTitle : $"{ancestorTitle} · {tail}",
            description,
            ancestor.Image ?? SiteCard,
            ancestor.Node.Path);
    }

    /// <summary>
    /// The requested path's segments BELOW <paramref name="ancestorPath"/>, joined for reading —
    /// exactly the text the sharer pasted, in their own spelling, and nothing else. Empty when the
    /// requested path IS that ancestor, or is not under it at all: the walk cannot produce the
    /// latter, and an empty tail is the one answer that invents nothing if it ever does.
    /// </summary>
    private static string TailBelow(string requestedPath, string ancestorPath)
    {
        var requested = requestedPath.Trim('/');
        var ancestor = ancestorPath.Trim('/');
        if (ancestor.Length == 0)
            return requested.Replace("/", SegmentSeparator);
        if (!requested.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase))
            return "";
        return requested[ancestor.Length..].Trim('/').Replace("/", SegmentSeparator);
    }

    /// <summary>Punctuation, not words: the path separator spaced out for a card, and the
    /// title/caption divider the page <c>&lt;title&gt;</c> already uses. Nothing here is language,
    /// so nothing here is translated.</summary>
    private const string SegmentSeparator = " / ";

    /// <summary>
    /// The strict ancestors of <paramref name="nodePath"/>, DEEPEST FIRST — the candidate order for
    /// "the nearest public ancestor". The node itself is excluded on purpose: this surface is only
    /// ever reached because the gate withheld it, and a card built from it would BE the leak.
    /// </summary>
    private static IEnumerable<string> AncestorPaths(string nodePath)
    {
        var segments = (nodePath ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var walked = 0;
        for (var depth = segments.Length - 1; depth > 0 && walked < MaxAncestorsWalked; depth--, walked++)
            yield return string.Join('/', segments.Take(depth));
    }

    /// <summary>
    /// The first of <paramref name="ancestors"/> the anonymous gate admits, or null when none is.
    /// <c>Concat</c> over a LAZY sequence with <c>Take(1)</c>: the next ancestor is resolved only
    /// because the previous one was withheld, and nothing above the hit is read at all.
    /// </summary>
    private static IObservable<SeoPageData?> NearestPublicAncestor(
        IMessageHub hub, IEnumerable<string> ancestors) =>
        ancestors
            .Select(ancestor => Resolve(hub, ancestor))
            .Concat()
            .Where(data => data is not null)
            .Take(1)
            .DefaultIfEmpty(null);

    /// <summary>
    /// The localized call to action for a gated path, or null when the partition offers no route in.
    /// A <see cref="PartitionAccessPolicy.RedirectOnDenied"/> is the owner SAYING there is one (a
    /// sign-up page, a course cover); without it, telling a reader to sign in would be advice that
    /// leads nowhere.
    ///
    /// <para>The redirect TARGET is deliberately not named on the card. It is not in the URL the
    /// sharer pasted, so printing it would disclose something new — and the link on the card already
    /// goes there, because that is what the redirect does to whoever clicks it.</para>
    ///
    /// <para>The policy read gets its own Catch: a faulted policy chain costs the sentence, not the
    /// whole card — the same fail-open-to-less-information this resolver applies throughout. A read
    /// that never emits is bounded by the caller's budget instead.</para>
    /// </summary>
    private static IObservable<string?> CallToAction(IMessageHub hub, string deniedPath, string? locale) =>
        hub.GetRedirectOnDenied(deniedPath)
            .Take(1)
            .Select(redirect => string.IsNullOrWhiteSpace(redirect)
                ? null
                : LocalizationCatalog.Get(CallToActionKey, locale))
            .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
            .DefaultIfEmpty(null);

    /// <summary>
    /// The node's document body as HTML, for <see cref="SeoPageData.Body"/>: the content's own
    /// <c>prerenderedHtml</c> when it carries one, else its markdown — <c>content</c> (a markdown
    /// node) or <c>body</c> (a plugin cover) — rendered through the SAME pipeline the portal renders
    /// it with, so the crawler reads what a visitor reads. Both content shapes (typed record,
    /// untyped JSON) resolve through <see cref="ContentString"/>. Null for content that is not a
    /// document. Pure: no hub, no IO.
    /// </summary>
    public static string? RenderBody(MeshNode node)
    {
        if (ContentString(node, "prerenderedHtml") is { Length: > 0 } prerendered)
            return prerendered;
        var markdown = FirstNonEmpty(ContentString(node, "content"), ContentString(node, "body"));
        return markdown is null
            ? null
            : MarkdownContent.Parse(markdown, node.Path, node.Path).PrerenderedHtml;
    }

    /// <summary>
    /// The page description for meta tags and the share card: the node's Description, else the
    /// content's <c>abstract</c>/<c>description</c>, else the sales copy a catalog root carries
    /// instead — <c>tagline</c>, <c>summary</c>, <c>headline</c> (the Store root has a headline and
    /// a tagline and no description, so its card and its <c>og:description</c> were empty). Both
    /// content shapes (typed record, untyped JSON) resolve through <see cref="ContentString"/>.
    /// </summary>
    public static string? ExtractDescription(MeshNode node) =>
        FirstNonEmpty(
            node.Description,
            ContentString(node, "abstract"),
            ContentString(node, "description"),
            ContentString(node, "tagline"),
            ContentString(node, "summary"),
            ContentString(node, "headline"));

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
        ExtractImage(node) ?? GeneratedCard(node.Path);

    /// <summary>
    /// The generated card's URL for one node path. The <c>.png</c> suffix is deliberate: some
    /// unfurlers (iMessage's LinkPresentation among them) weigh an extension when deciding whether
    /// an <c>og:image</c> is a picture, and the route accepts the suffix.
    /// </summary>
    public static string GeneratedCard(string nodePath) => $"/api/og/{nodePath.Trim('/')}.png";

    /// <summary>The card for a page that is no public node (the home page, a private node) — the
    /// instance's own card, saying only its name and host.</summary>
    public const string SiteCard = "/api/og.png";

    /// <summary>Whether a share image is one the portal DRAWS — those are always
    /// <see cref="OgCardRenderer.Width"/>×<see cref="OgCardRenderer.Height"/> PNGs, so the head
    /// can declare the size; an authored image's dimensions are unknown here.
    ///
    /// <para>Takes the image as <see cref="ShareImage"/> returns it, BEFORE the head prefixes the
    /// host: the drawn cards are the two ROOT-RELATIVE shapes this route serves and nothing else.
    /// An authored absolute URL that happens to contain <c>/api/og/</c> on some other host is not
    /// the portal's card, and a substring test would have declared its size as if it were.</para>
    /// </summary>
    public static bool IsGeneratedCard(string? image) =>
        image is not null
        && (image.StartsWith("/api/og/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(image, SiteCard, StringComparison.OrdinalIgnoreCase));

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
    /// the access-controlled content URL, a URL stays that URL, and an inline <c>&lt;svg&gt;</c>
    /// goes through the same backplate policy the app applies (<see cref="ResolveIconSvg"/>), which
    /// leaves an icon that paints its own plate exactly as authored — so the tab, the card and the
    /// app can never disagree about what a node looks like.</para>
    /// </summary>
    /// <param name="node">The node whose page is being served.</param>
    /// <returns>Its own icon, or null when it carries none an <c>href</c> can point at.</returns>
    public static PageIcon? ResolveIcon(MeshNode node)
    {
        // An inline <svg> is MARKUP, not a location, so it travels as a data URI — the same
        // mechanism App.razor already uses for the per-instance favicon. The markup is the node's
        // icon through the backplate policy and nothing else (see ResolveIconSvg): an authored
        // plate survives byte for byte, and one that has none gets the same generated plate the app
        // draws, because a tab strip is a ground this process does not control.
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
    ///
    /// <para>🚨 It goes through the backplate policy (<see cref="IconBackplate.Ensure"/>) — the
    /// same policy the in-app icon passes (<see cref="MeshNodeImageHelper.ResolveRenderable"/>), so
    /// "the tab, the card and the app can never disagree" stays true. It is not cosmetic here
    /// (#4350): BOTH consumers of this value render on a ground this process does not control, and
    /// neither of them has any surrounding text for a <c>currentColor</c> outline to inherit from.
    /// In a <c>data:</c> URI the markup is a DOCUMENT, so such an icon paints in the initial color;
    /// and <see cref="IconRasterizer.Render"/> leaves its canvas transparent precisely BECAUSE it
    /// was told every mark carries its own plate. Measured on the pixels: a black hairline on
    /// nothing — invisible in a dark tab strip and on a dark link-preview card, and served behind a
    /// 200, so nothing reports it. An icon that already paints a full-bleed plate — every authored
    /// store mark, every thread identicon — is returned byte-identical, so nothing that worked
    /// before changes.</para>
    /// </summary>
    /// <param name="node">The node whose page is being served.</param>
    public static string? ResolveIconSvg(MeshNode node)
    {
        var icon = MeshNodeImageHelper.ResolveContentPath(node.Icon, node.Path);
        return MeshNodeImageHelper.IsInlineSvg(icon) ? IconBackplate.Ensure(icon!) : null;
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

    /// <summary>
    /// A string-to-string map member of the node's content, by camelCase JSON name (both shapes) —
    /// e.g. a course's <c>translations</c> (<c>{"de-CH": "AgenticPrimerDe"}</c>), which the head
    /// turns into <c>hreflang</c> links. Empty when absent or not a map of strings.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ContentStringMap(MeshNode node, string property)
    {
        switch (node.Content)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } je
                when je.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object:
                return value.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                    .ToImmutableDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
            case JsonElement or null:
                return ImmutableDictionary<string, string>.Empty;
            default:
                return TypedMember(node.Content, property) switch
                {
                    IEnumerable<KeyValuePair<string, string>> typed =>
                        typed.ToImmutableDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                    _ => ImmutableDictionary<string, string>.Empty,
                };
        }
    }

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
