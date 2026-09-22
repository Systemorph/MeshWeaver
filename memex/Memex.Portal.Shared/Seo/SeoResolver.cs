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
    /// <para>The authored markdown is the source of truth, rendered through the same renderer
    /// as the interactive view. Cached HTML is a fallback only when the source is absent:
    /// neither stored HTML field records which source or renderer produced it, so preferring
    /// either can resurrect an old page after an edit. Null when the node has no
    /// document-shaped content (a data node, a pure layout-area page). Only ever produced for a
    /// node the <see cref="AnonymousGate"/> admitted, like every other member here — a gated
    /// chapter is refused before this is computed.</para>
    /// </summary>
    public string? Body => SeoResolver.RenderBody(Node);
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
/// ancestor and is already served to anyone who asks: its name, its description, and its share image
/// — whatever it authored, else its drawn <c>/api/og/{ancestor}.png</c> — which the same gate already
/// serves anonymously, so an unfurler can fetch what the head declares. The withheld node's
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
/// 🚨 THE CARD A GATED PAGE SHARES WHEN ITS OWNER OPTED IN — the node's OWN name, its own authored
/// summary, its own share image and its own mark, for a page whose CONTENT a logged-out visitor
/// still may not read.
///
/// <para><b>Opt-in, default off.</b> It is produced only where
/// <see cref="PartitionAccessPolicy.PublicPreview"/> is set on the node's scope or an ancestor's;
/// with the flag unset — every partition, until someone sets it — a gated page behaves exactly as it
/// did, which is the control that matters. What it answers is the question a partition owner keeps
/// asking of a gated link: <i>couldn't it say the name of the node, and the description, and the
/// icon?</i> It can, once they say so.</para>
///
/// <para>🚨 <b>This IS a disclosure, and that is the point.</b> A link into an opted-in partition
/// tells whoever holds it what the page is CALLED and what it is ABOUT. That is why it is not a
/// default and never inferred: the precedent is <c>/health</c>, which was deliberately changed to
/// print the PARTITION and not the node's own name (#4258/#3890) because a control that works by
/// disclosing other people's node titles is a disclosure surface wearing an instrument's colours.
/// The difference here is consent — the owner of the data states it, per scope, in the same
/// <c>_Policy</c> that governs everything else about that subtree.</para>
///
/// <para><b>What it deliberately cannot carry:</b> the node itself, and therefore the BODY. The
/// crawler-facing body component renders a <see cref="SeoPageData"/>, which only
/// <see cref="SeoResolver.Resolve"/> produces and which this page is still refused — so the opt-in
/// cannot be widened into serving content by a caller that misreads it.</para>
/// </summary>
/// <param name="Title">The node's own name (its id when it has no name).</param>
/// <param name="Description">The node's AUTHORED summary, plus the partition's call to action when
/// it declares a <see cref="PartitionAccessPolicy.RedirectOnDenied"/>. Null when it has neither.
/// Never the body — see <see cref="SeoResolver.ResolvePreview"/>.</param>
/// <param name="Image">The node's own share image, exactly as <see cref="SeoResolver.ShareImage"/>
/// returns it: authored when it declares one, else its drawn <c>/api/og/{path}.png</c>. The routes
/// serve it under the same flag (<see cref="SeoResolver.ResolveShareableNode"/>), so what the head
/// declares can actually be fetched.</param>
/// <param name="NodePath">The node the card describes — what a log line and a test name.</param>
/// <param name="Icons">The node's own icon links, as <see cref="SeoResolver.ResolveIconLinks"/>
/// builds them for a public page: empty for a node with no mark of its own.</param>
public sealed record SeoPreviewCard(
    string Title,
    string? Description,
    string Image,
    string NodePath,
    IReadOnlyList<PageIcon> Icons);

/// <summary>
/// A node whose PICTURE the share-card and icon routes may draw, and WHICH decision cleared it.
///
/// <para>🚨 The second field is not bookkeeping — it decides the response's cache directive, and
/// getting that wrong makes a revocable disclosure unrevocable. A node the
/// <see cref="AnonymousGate"/> admits is <c>public, max-age=…</c> cacheable: everything drawn on its
/// card is already served to anonymous callers on the page itself, and crawlers refetch cards
/// aggressively. A node cleared only by <see cref="PartitionAccessPolicy.PublicPreview"/> is NOT,
/// because a policy is revocable and a shared cache never re-asks the origin — so it is served
/// <c>private, no-store</c>, and flipping the flag off stops the origin serving it on the next
/// request instead of up to a day later.</para>
///
/// <para>🚨 What that does NOT reach is the UNFURLER's own copy: Slack, Teams, iMessage and LinkedIn
/// keep a preview for hours to days and no response header controls it (the same reason a fixed page
/// can take hours to re-scrape). Revocation is therefore immediate at the origin and
/// eventually-consistent at the consumer, and <c>Doc/Architecture/LinkPreviews</c> says so where an
/// owner reads about the flag.</para>
/// </summary>
/// <param name="Node">The node whose picture may be drawn.</param>
/// <param name="AnonymousReadable">True when the gate admitted it — the shared-cacheable case. False
/// when only the preview opt-in cleared it.</param>
public sealed record ShareableNode(MeshNode Node, bool AnonymousReadable);

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
        var access = hub.ServiceProvider.GetService<AccessService>();
        var caller = access?.Context ?? access?.CircuitContext;
        return ResolveGated(hub, path)
            // The permission fold can emit outside the HTTP/circuit execution context. Carry
            // the actual caller into the owner read; neither the anonymous verdict nor the
            // emission thread's identity is a replacement for the requesting viewer.
            .CarryAccessContext(hub.ServiceProvider, caller)
            .SelectMany(gated => gated is not { Readable: true } readable
                ? Observable.Return<SeoPageData?>(null)
                // Path resolution is discovery and may carry an older query snapshot. Read the
                // admitted node from its owner, then recheck anonymous access: a policy may have
                // changed during that read, and a signed-in caller's read grant is not publicness.
                : hub.GetMeshNode(readable.Resolution.Prefix)
                    .SelectMany(node => node is null
                        ? Observable.Return<SeoPageData?>(null)
                        : AnonymousGate.AllowAnonymous(hub, readable.Resolution.Prefix)
                            .Take(1)
                            .Select(allowed => allowed
                                ? new SeoPageData(node, ExtractDescription(node), ShareImage(node))
                                {
                                    Remainder = string.IsNullOrEmpty(readable.Resolution.Remainder)
                                        ? null
                                        : readable.Resolution.Remainder,
                                }
                                : null)))
            .Timeout(ResolveBudget)
            .Catch<SeoPageData?, Exception>(_ => Observable.Return<SeoPageData?>(null));
    }

    /// <summary>
    /// 🚨 THE ONE RESOLVE-AND-GATE PASS. Every SEO surface — the page head, the share card route,
    /// the icon route — asks this and nothing else, so there is no second permission rule here to
    /// drift from the page's. It answers TWO facts and keeps them apart: the node the URL names, and
    /// whether the <see cref="AnonymousGate"/> admits it.
    ///
    /// <para><b>The node is returned whether or not the gate admits it, and that is not a leak</b> —
    /// it is the same shape this has always had (the gate is evaluated AFTER the resolution because
    /// <see cref="IPathResolver"/> is the router's own literal resolution and is not access-filtered).
    /// What decides disclosure is what each CALLER does with <c>Readable</c>: <see cref="Resolve"/>
    /// discards the node entirely when it is false, and the preview path below discloses only what
    /// <see cref="PartitionAccessPolicy.PublicPreview"/> opts in.</para>
    ///
    /// <para>Raw: no time bound and no Catch — every public entry point applies its own
    /// <see cref="ResolveBudget"/> and fail-open, exactly as before.</para>
    /// </summary>
    private static IObservable<GatedNode?> ResolveGated(IMessageHub hub, string path)
    {
        var resolver = hub.ServiceProvider.GetService<IPathResolver>();
        if (resolver is null)
            return Observable.Return<GatedNode?>(null);
        return resolver.ResolvePath((path ?? "").Trim('/'))
            .Take(1)
            .SelectMany(resolution => resolution?.Node is not { } node
                ? Observable.Return<GatedNode?>(null)
                // The BOOLEAN projection is correct here and stays (#2901): "not public" and "the
                // gate could not find out" both mean WITHHOLD the rich metadata, and neither is
                // asserted to a human — an omitted og: block is not a claim about the visitor. A
                // caller whose answer becomes a redirect, a status code or a message must use
                // AnonymousGate.Evaluate and branch on IsUndetermined first.
                : AnonymousGate.AllowAnonymous(hub, resolution.Prefix)
                    .Take(1)
                    .Select(allowed => (GatedNode?)new GatedNode(resolution, node, allowed)));
    }

    /// <summary>What one URL resolved to, and whether a logged-out visitor may read it.</summary>
    /// <param name="Resolution">The literal path resolution — <c>Prefix</c> is the scope every
    /// policy and permission question below is asked about.</param>
    /// <param name="Node">The node the URL names, gated or not.</param>
    /// <param name="Readable">The <see cref="AnonymousGate"/>'s fail-closed verdict.</param>
    private sealed record GatedNode(AddressResolution Resolution, MeshNode Node, bool Readable);

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
    /// 🚨 THE NODE'S OWN CARD FOR A GATED PAGE — and it exists ONLY because the partition's owner
    /// switched <see cref="PartitionAccessPolicy.PublicPreview"/> on. Emits null for every path that
    /// is readable anyway (the caller already has <see cref="Resolve"/> for those), for every path
    /// whose scope has not opted in — which is every scope by default — and for anything that errors
    /// or times out. Cold.
    ///
    /// <para><b>What it discloses, exhaustively:</b> the node's <c>Name</c>, its AUTHORED summary
    /// (<see cref="ExtractDescription"/> — <c>Description</c>, else <c>abstract</c>/<c>description</c>/
    /// <c>tagline</c>/<c>summary</c>/<c>headline</c>), its share image, and its own icon links.
    /// <b>Never its body:</b> this returns no <see cref="SeoPageData"/> and carries no
    /// <see cref="MeshNode"/>, so the body the crawler-facing <c>SeoPrerenderedBody</c> renders
    /// cannot be reached from it — that component reads a <c>SeoPageData</c>, which only
    /// <see cref="Resolve"/> produces, and <see cref="Resolve"/> still refuses this node.</para>
    ///
    /// <para>🚨 <b>The summary is an authored summary, not the first line of the body.</b> That is a
    /// property of <see cref="ExtractDescription"/>, which reads six summary members and never
    /// <c>content</c> or <c>body</c>; <see cref="RenderBody"/> is the only thing that touches those,
    /// and nothing here calls it. If a future member is added to the description chain, it must be
    /// an authored summary for the same reason.</para>
    ///
    /// <para>The node stays OUT of <c>/sitemap.xml</c> and off the public host, because both read
    /// <see cref="Resolve"/> (via <c>PublicSite</c>) and neither reads this — unfurlable is not
    /// indexable, and the head states <c>noindex</c> to say so.</para>
    /// </summary>
    /// <param name="hub">The hub whose path resolver, gate and policy chain answer.</param>
    /// <param name="path">The node path the visitor asked for.</param>
    /// <param name="locale">The VIEWER's language tag, read explicitly off their AccessContext by
    /// the caller — never from an ambient culture. Null ⇒ English.</param>
    public static IObservable<SeoPreviewCard?> ResolvePreview(
        IMessageHub hub, string path, string? locale = null) =>
        ResolveGated(hub, path)
            .SelectMany(gated => gated is not { Readable: false } withheld
                ? Observable.Return<SeoPreviewCard?>(null)
                : hub.GetPublicPreview(withheld.Resolution.Prefix)
                    .Take(1)
                    .SelectMany(optedIn => optedIn
                        ? CallToAction(hub, withheld.Resolution.Prefix, locale)
                            .Select(callToAction => ComposePreviewCard(withheld.Node, callToAction))
                        : Observable.Return<SeoPreviewCard?>(null)))
            .Timeout(ResolveBudget)
            .Catch<SeoPreviewCard?, Exception>(_ => Observable.Return<SeoPreviewCard?>(null));

    /// <summary>
    /// The static-SSR boundary bridge for <see cref="ResolvePreview"/> — <c>ObserveCompletion</c>,
    /// never <c>.ToTask()</c>, for the reason <see cref="ResolveAsync"/> states.
    /// </summary>
    /// <param name="hub">The hub whose path resolver, gate and policy chain answer.</param>
    /// <param name="path">The node path the visitor asked for.</param>
    /// <param name="locale">The viewer's language tag; null ⇒ English.</param>
    public static Task<SeoPreviewCard?> ResolvePreviewAsync(
        IMessageHub hub, string path, string? locale = null)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SeoResolver));
        return ResolvePreview(hub, path, locale)
            .FirstAsync()
            .ObserveCompletion(ex => logger?.LogWarning(
                ex,
                "The public-preview card for '{Path}' faulted after the head had already been produced",
                path));
    }

    /// <summary>
    /// The card, from the node and nothing else. Pure — no hub, no IO — so everything a previewed
    /// page can possibly say is decided here and is testable without a mesh.
    /// </summary>
    /// <param name="node">The node whose page was withheld, on a scope that opted in.</param>
    /// <param name="callToAction">The localized sentence to append, or null for none.</param>
    public static SeoPreviewCard ComposePreviewCard(MeshNode node, string? callToAction)
    {
        ArgumentNullException.ThrowIfNull(node);
        var summary = FirstNonEmpty(ExtractDescription(node));
        return new SeoPreviewCard(
            node.Name ?? node.Id,
            summary is null
                ? callToAction
                : callToAction is null ? summary : $"{summary} {callToAction}",
            PreviewImage(node),
            node.Path,
            PreviewIconLinks(node));
    }

    /// <summary>
    /// 🚨 THE PICTURE A PREVIEWED PAGE MAY DECLARE — and it is NOT always
    /// <see cref="ShareImage"/>, which is the whole point of this method existing.
    ///
    /// <para>An authored image is typically <c>/api/content/{partition}/content/og.png</c>: the
    /// portal's own <b>access-controlled</b> content route, which this opt-in deliberately does not
    /// open — that route serves file BYTES, not the four strings the flag consents to. Declaring it
    /// for a gated node would promise an <c>og:image</c> that anonymous unfurlers receive a 404 for,
    /// which is WORSE than no card: several drop the whole preview when the promised picture does not
    /// fetch. So a root-relative authored image falls back to the DRAWN card, which
    /// <see cref="ResolveShareableNode"/> does serve under the same flag.</para>
    ///
    /// <para>An ABSOLUTE authored image is kept: it is some other host's business, fetchable or not on
    /// its own terms, and nothing here can make it worse. A PUBLIC page is untouched by this and keeps
    /// declaring exactly what it declares today — its authored image is fetchable precisely because
    /// the gate admits the node.</para>
    /// </summary>
    /// <param name="node">The withheld node, on a scope that opted in.</param>
    private static string PreviewImage(MeshNode node) =>
        ExtractImage(node) is { } authored
        && authored.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? authored
            : GeneratedCard(node.Path);

    /// <summary>
    /// 🚨 THE ICON CHANNELS A PREVIEWED PAGE MAY DECLARE: only the ones that are anonymously
    /// fetchable BY CONSTRUCTION — the inline-svg <c>data:</c> URI, which is self-contained and
    /// carries nothing beyond the mark itself, and the <c>/api/icon/{path}.png</c> rasters, which
    /// honour this same flag.
    ///
    /// <para>A node whose icon is a <c>content:</c> reference resolves to <c>/api/content/…</c>, still
    /// <c>Read</c>-gated, and <see cref="ResolveIconLinks"/> returns that ONE link and no raster
    /// channels for it — so a previewed page would publish exactly one icon link and it would be
    /// broken. Such a node gets NO icon link here, which is the same honest fallback the icon route
    /// already documents for a node with no usable mark: the portal favicon stays, rather than a link
    /// that 404s.</para>
    /// </summary>
    /// <param name="node">The withheld node, on a scope that opted in.</param>
    private static IReadOnlyList<PageIcon> PreviewIconLinks(MeshNode node) =>
        ResolveIconLinks(node)
            .Where(link =>
                link.Href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || link.Href.StartsWith("/api/icon/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// 🚨 THE ONE PREDICATE THE IMAGE ROUTES ASK — the node whose picture <c>/api/og/{path}.png</c>
    /// and <c>/api/icon/{path}.png</c> may draw: one the gate admits, or one whose scope opted in to
    /// <see cref="PartitionAccessPolicy.PublicPreview"/>. Null ⇒ 404, which is also what a missing
    /// node answers, so the routes still cannot be used as an existence oracle.
    ///
    /// <para>It exists so the routes and the HEAD cannot disagree: a head that declares
    /// <c>og:image</c> for a previewed page while the route 404s would ship a card with a broken
    /// picture — worse than no card, because several unfurlers drop the whole preview when the image
    /// they were promised does not fetch. Everything those two routes draw is exactly what the head
    /// discloses (name, authored summary, category, mark, price chip, instance + path) and never the
    /// body, which is why one flag can govern both.</para>
    ///
    /// <para>The policy read happens only on the REFUSED leg, so a public page costs exactly what it
    /// costs today and a path that names no node costs no policy read at all.</para>
    ///
    /// <para>🚨 It answers WHICH decision cleared the response, not just that one did, because the two
    /// are cacheable differently — see <see cref="ShareableNode.AnonymousReadable"/>.</para>
    /// </summary>
    /// <param name="hub">The hub whose path resolver, gate and policy chain answer.</param>
    /// <param name="path">The node path the picture was asked for.</param>
    public static IObservable<ShareableNode?> ResolveShareableNode(IMessageHub hub, string path) =>
        ResolveGated(hub, path)
            .SelectMany(gated => gated is not { } resolved
                ? Observable.Return<ShareableNode?>(null)
                : resolved.Readable
                    ? Observable.Return<ShareableNode?>(new ShareableNode(resolved.Node, true))
                    : hub.GetPublicPreview(resolved.Resolution.Prefix)
                        .Take(1)
                        .Select(optedIn => optedIn ? new ShareableNode(resolved.Node, false) : null))
            .Timeout(ResolveBudget)
            .Catch<ShareableNode?, Exception>(_ => Observable.Return<ShareableNode?>(null));

    /// <summary>
    /// The node's current document body as HTML, for <see cref="SeoPageData.Body"/>. Authored
    /// markdown — <c>content</c>, <c>body</c>, or a bare string — always wins over cached HTML,
    /// including an empty source after the author clears a page. The interactive view's
    /// <see cref="MarkdownViewLogic.Render"/> owns the rendering and relative-link resolution.
    /// Only a node without source falls back to its mirrored HTML or its content's
    /// <c>prerenderedHtml</c>. Null for content that is not a document. Pure: no hub, no IO.
    /// </summary>
    public static string? RenderBody(MeshNode node) => MarkdownBody.Render(node);

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
