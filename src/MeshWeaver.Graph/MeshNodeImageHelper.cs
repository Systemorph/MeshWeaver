using System.Collections.Immutable;
using System.Globalization;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using DomainIcon = MeshWeaver.Domain.Icon;

namespace MeshWeaver.Graph;

/// <summary>
/// How an icon value has to be put on the page. A node's <c>Icon</c> is a single string that can
/// legitimately hold three mutually-incompatible things, and rendering one through the other's
/// element is what produces a BROKEN icon — an <c>&lt;img src="&lt;svg …"&gt;</c> (the identicon every
/// thread gets from <c>ThreadIconGenerator</c>) or an <c>&lt;img src="Document"&gt;</c> (a Fluent name).
/// </summary>
public enum IconRenderKind
{
    /// <summary>An image URL or <c>data:</c> URI — render as <c>&lt;img src="…"&gt;</c>.</summary>
    Image,

    /// <summary>Raw inline <c>&lt;svg&gt;</c> markup — render verbatim, NEVER as an img source.</summary>
    InlineSvg,

    /// <summary>A text glyph (emoji) — render as text.</summary>
    Glyph,
}

/// <summary>
/// An icon value together with the element it has to be rendered with. Produced by
/// <see cref="MeshNodeImageHelper.ResolveRenderable"/>, which guarantees the pair is renderable.
/// </summary>
/// <param name="Kind">The element the value has to be rendered with.</param>
/// <param name="Value">The icon value — a URL, inline svg markup, or a text glyph, per <paramref name="Kind"/>.</param>
public readonly record struct RenderableIcon(IconRenderKind Kind, string Value);

/// <summary>
/// An icon ready to hang off a <c>&lt;link rel="icon"&gt;</c>: a value an <c>href</c> can carry,
/// plus the media type to declare with it.
/// </summary>
/// <param name="Href">A URL the portal already serves (a shipped glyph, an access-controlled
/// content file), or an inline <c>data:</c> URI for an icon whose value is MARKUP rather than a
/// location (raw <c>&lt;svg&gt;</c>, a text glyph).</param>
/// <param name="Type">The media type to declare, or null when the value alone does not pin one
/// down — declaring the WRONG type is worse than declaring none, because a browser ranking several
/// icons by type would then rank this one on a lie.</param>
public readonly record struct IconLink(string Href, string? Type);

/// <summary>
/// Helper for resolving and classifying a mesh node's icon for rendering. Resolves
/// content: references to absolute URLs, supplies NodeType default icons, and detects
/// whether an icon value is inline SVG, an image URL/data URI, an emoji, or a legacy
/// Fluent icon name.
/// </summary>
public static class MeshNodeImageHelper
{
    /// <summary>
    /// Returns the icon value for rendering. Accepts URLs, data URIs, inline SVG, and emojis.
    /// Returns null only for legacy Fluent icon names (PascalCase ASCII, e.g. "Document").
    /// Callers are responsible for detecting the type (SVG, URL, emoji) and rendering appropriately.
    /// </summary>
    public static string? GetIconForRendering(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
            return null;
        // Filter out legacy Fluent icon names (PascalCase ASCII words like "Document", "People")
        if (IsFluentIconName(icon))
            return null;
        return icon;
    }

    /// <summary>
    /// Resolves a node's icon for rendering, handling content: references relative to the node path.
    /// E.g., "content:icon.svg" on node "Org/Project" → "/api/content/Org/Project/content/icon.svg".
    /// When the node carries no icon of its own, falls back to a default icon for its
    /// <see cref="MeshNode.NodeType"/> (<see cref="DefaultIconForNodeType"/>) so every node reads as
    /// its type rather than a bare letter — Markdown → document, Code → code, Agent → bot, etc.
    ///
    /// <para>This overload inherits NOTHING: it is the resolution for a caller that has only the one
    /// node in hand. A caller that also holds the node's partition root — a page, a tab — passes it
    /// to <see cref="ResolveNodeIcon(MeshNode?, MeshNode?)"/> and gets the package's mark instead of
    /// a generic type glyph.</para>
    /// </summary>
    public static string? ResolveNodeIcon(MeshNode? node) => ResolveNodeIcon(node, null);

    /// <summary>
    /// 🚨 THE RESOLUTION WITH PACKAGE-ROOT INHERITANCE — a node that carries no mark of its own
    /// wears its PACKAGE's, ahead of the generic glyph for its type (issue #2075, item 2).
    ///
    /// <para><b>Why.</b> A lesson under <c>AgenticEngineering</c>, a game under <c>Chess</c>, a doc
    /// under a store package: none of them authors an icon, so all of them resolved to the same
    /// <c>document</c> / <c>box</c> chrome — in the page header and, worse, in the browser tab,
    /// where "which package is this" is the only thing the 16 px square can usefully say. The
    /// package roots DO carry marks, aligned to one visual language precisely so they read at that
    /// size (MeshWeaver.Plugins #588).</para>
    ///
    /// <para><b>Only the root's OWN mark is inherited</b> — <see cref="MeshNode.Icon"/>, resolved
    /// the same two ways a node's own icon is (a <c>content:</c> reference, a shipped glyph of that
    /// name). Deliberately NOT the root's full chain: falling through to the root's NodeType default
    /// would dress every doc under an unmarked package in the ROOT's type glyph instead of its own,
    /// which is strictly worse than what it replaces.</para>
    ///
    /// <para><b>A partition root never inherits</b> — <see cref="PartitionRootPath"/> is null for a
    /// single-segment path, so a root cannot resolve itself, and a supplied root that is not
    /// actually this node's partition root is ignored rather than borrowed from.</para>
    ///
    /// <para><b>Still total</b>, exactly as the one-argument overload is: own icon → shipped glyph
    /// of that name → the partition root's own mark → the NodeType default → the neutral box. Every
    /// node resolves to something, so no card, tab or avatar can fall back to a bare initial.</para>
    /// </summary>
    /// <param name="node">The node whose icon is being resolved.</param>
    /// <param name="partitionRoot">The node's partition root, when the caller has it — the node at
    /// the FIRST segment of <paramref name="node"/>'s path. Null (the default, and the one-argument
    /// overload) simply skips the inheritance step.</param>
    public static string? ResolveNodeIcon(MeshNode? node, MeshNode? partitionRoot) =>
        node == null ? null
            // Guarantee a resolved icon for ANY non-null node so a card / avatar NEVER falls back to
            // the bare-initial (blue) placeholder: own Icon → the shipped glyph of the same name →
            // the partition root's own mark → NodeType default → a neutral box glyph (covers a
            // typeless node, where DefaultIconForNodeType returns null).
            : ResolveContentPath(node.Icon, node.Path)
              ?? ShippedIconFor(node.Icon)
              ?? InheritedIcon(node, partitionRoot)
              ?? DefaultIconForNodeType(node.NodeType)
              ?? NeutralIconUrl;

    /// <summary>
    /// The PARTITION ROOT path of a mesh path — its first segment — or null when there is no
    /// distinct root to inherit from.
    ///
    /// <para>Null in exactly two cases, and both mean "nothing above this to inherit": an empty
    /// path, and a path that IS a single segment. The second is the load-bearing one — a partition
    /// root's own root is itself, and inheriting from yourself is a no-op at best and, once the
    /// value is fed back through the chain, a way to make <c>Chess</c> resolve <c>Chess</c>
    /// forever.</para>
    /// </summary>
    /// <param name="nodePath">A mesh node path, e.g. <c>AgenticEngineering/Lesson1</c>.</param>
    public static string? PartitionRootPath(string? nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
            return null;
        var segments = nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 1 ? null : segments[0];
    }

    /// <summary>
    /// The mark <paramref name="node"/> inherits from <paramref name="partitionRoot"/>, or null when
    /// it inherits nothing.
    ///
    /// <para>🚨 The relationship is VERIFIED, not assumed: the supplied root is used only when its
    /// path really is the first segment of the node's. A caller that hands over the wrong node —
    /// the parent instead of the root, a stale frame from another page — gets no inheritance rather
    /// than an unrelated package's mark on someone else's page, which is the failure that would be
    /// invisible in a screenshot.</para>
    /// </summary>
    /// <param name="node">The node that would inherit.</param>
    /// <param name="partitionRoot">The candidate partition root; null yields null.</param>
    public static string? InheritedIcon(MeshNode? node, MeshNode? partitionRoot)
    {
        if (node is null || partitionRoot is null)
            return null;
        if (PartitionRootPath(node.Path) is not { } rootPath
            || !string.Equals(rootPath, (partitionRoot.Path ?? "").Trim('/'),
                StringComparison.OrdinalIgnoreCase))
            return null;
        // The root's OWN mark only — never its NodeType default, see ResolveNodeIcon's remarks.
        return ResolveContentPath(partitionRoot.Icon, partitionRoot.Path)
               ?? ShippedIconFor(partitionRoot.Icon);
    }

    /// <summary>
    /// The shipped glyph matching a FLUENT ICON NAME, or null when none is shipped under that name.
    ///
    /// <para>🚨 Without this a Fluent name resolved to NOTHING and every node carrying one fell
    /// through to its NodeType default — which is why every Skill in the Store rendered the same
    /// <c>sparkle</c> regardless of the icon it declared. Skills author Fluent names because that
    /// is what the nav and the chat composer render (<c>NavLink</c> resolves them through
    /// <c>Icon.ToFluentIcon</c>); but a card built as an HTML string has no Blazor component to
    /// render one with, so it needs a URL. Matching the name against the icons this assembly
    /// already ships gives it one.</para>
    ///
    /// <para>The set is read from the assembly's own manifest at type-init and never written, so
    /// dropping a new <c>Icons/*.svg</c> in makes that name resolve with no list to update.</para>
    /// </summary>
    public static string? ShippedIconFor(string? icon) =>
        !string.IsNullOrEmpty(icon) && IsFluentIconName(icon)
        && ShippedIconNames.Contains(icon.ToLowerInvariant())
            ? $"/static/NodeTypeIcons/{icon.ToLowerInvariant()}.svg"
            : null;

    private const string IconResourcePrefix = "MeshWeaver.Graph.Icons.";

    /// <summary>
    /// Every glyph this assembly ships, lower-cased. An immutable, read-only lookup built once from
    /// the manifest — a constant, not a cache: nothing writes to it at runtime.
    /// </summary>
    private static readonly System.Collections.Immutable.ImmutableHashSet<string> ShippedIconNames =
        typeof(MeshNodeImageHelper).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(IconResourcePrefix, StringComparison.Ordinal)
                           && name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[IconResourcePrefix.Length..^".svg".Length].ToLowerInvariant())
            .ToImmutableHashSet();

    /// <summary>The neutral glyph used when a node has no own icon and its NodeType has no mapping.</summary>
    private const string NeutralNodeIcon = "box";

    /// <summary>
    /// The URL of <see cref="NeutralNodeIcon"/> — the last-resort glyph that makes
    /// <see cref="ResolveRenderable"/> total. A constant, not a cache: written once, never at runtime.
    /// </summary>
    public static readonly string NeutralIconUrl = $"/static/NodeTypeIcons/{NeutralNodeIcon}.svg";

    /// <summary>
    /// The first of <paramref name="icon"/> → <paramref name="fallback"/> → <see cref="NeutralIconUrl"/>
    /// that can ACTUALLY be rendered, paired with the element to render it with. Total: it always
    /// returns something renderable, so no caller can produce a broken icon.
    ///
    /// <para>🚨 This exists because a <c>MeshNode.Icon</c> is ONE string holding four different things
    /// (<see cref="DomainIcon.Parse"/>), and the obvious <c>&lt;img src="@node.Icon"&gt;</c> is correct for
    /// exactly one of them. Every thread carries a <c>ThreadIconGenerator</c> identicon — raw inline
    /// <c>&lt;svg&gt;</c> — so a sub-thread chip built that way rendered the browser's broken-image glyph,
    /// and the picker "fixed" it by dropping the icon entirely. A legacy Fluent NAME breaks the same
    /// way; it resolves here only when this assembly ships a glyph of that name
    /// (<see cref="ShippedIconFor"/>), otherwise the fallback wins rather than a broken image.</para>
    /// </summary>
    /// <param name="icon">The node's own icon value (any of the four forms; null/empty is fine).</param>
    /// <param name="fallback">The icon to use when <paramref name="icon"/> is absent or unrenderable —
    /// typically the NodeType's standard glyph (e.g. <c>/static/NodeTypeIcons/chat.svg</c> for a thread).</param>
    public static RenderableIcon ResolveRenderable(string? icon, string? fallback = null)
        => TryClassify(icon)
           ?? TryClassify(fallback)
           ?? new RenderableIcon(IconRenderKind.Image, NeutralIconUrl);

    /// <summary>
    /// Classifies one candidate, or null when it cannot be rendered at all (empty, or a Fluent icon
    /// name this assembly ships no glyph for) and the next candidate should be tried.
    /// </summary>
    private static RenderableIcon? TryClassify(string? value)
    {
        var parsed = DomainIcon.Parse(value);
        return parsed?.Provider switch
        {
            // Inline svg passes through the backplate policy: an icon without a full-bleed plate
            // gets a generated one here, at the ONE seam every surface classifies through, so a
            // currentColor outline or a dark pictorial can never render invisibly on one theme
            // (IconBackplate). Icons that already paint a plate — every authored store mark, every
            // thread identicon — pass through byte-identical.
            DomainIcon.InlineSvgProvider => new RenderableIcon(
                IconRenderKind.InlineSvg, IconBackplate.Ensure(parsed.Id)),
            DomainIcon.UrlProvider => new RenderableIcon(IconRenderKind.Image, parsed.Id),
            DomainIcon.TextProvider => new RenderableIcon(IconRenderKind.Glyph, parsed.Id),
            DomainIcon.FluentProvider when ShippedIconFor(parsed.Id) is { } url
                => new RenderableIcon(IconRenderKind.Image, url),
            _ => null,
        };
    }

    /// <summary>
    /// A default icon for a node that carries none of its own, keyed by its NodeType (the type node's
    /// path — the last segment is matched, so both <c>"Markdown"</c> and a fully-qualified type path
    /// resolve). Returns a <c>/static/NodeTypeIcons/*.svg</c> path; unknown/typeless nodes fall back
    /// to a neutral box. Mirrors the icons the built-in NodeType definitions declare, so an instance
    /// reads the same as its type.
    /// </summary>
    public static string? DefaultIconForNodeType(string? nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
            return null;
        var slash = nodeType.LastIndexOf('/');
        var name = (slash < 0 ? nodeType : nodeType[(slash + 1)..]).ToLowerInvariant();
        var icon = name switch
        {
            "markdown" or "document" => "document",
            "code" => "code",
            "agent" or "harness" => "bot",
            "languagemodel" or "aisettings" or "command" or "threadcomposer" or "skill" => "sparkle",
            "modelprovider" => "key",
            "thread" => "chat",
            "threadmessage" => "message",
            "comment" => "comment",
            "notification" => "bell",
            "approval" => "checkmark",
            "group" or "groupmembership" => "people",
            "user" or "vuser" or "person" => "person",
            "role" or "accessassignment" or "partitionaccesspolicy" => "shield",
            "email" => "mail",
            "space" or "organization" => "organization",
            "nodetype" => "box",
            _ => "box", // unknown/custom type → a neutral node glyph (never a bare letter)
        };
        return $"/static/NodeTypeIcons/{icon}.svg";
    }

    /// <summary>
    /// Resolves a user-entered image path to an absolute URL, interpreting bare
    /// <c>content:filename.ext</c> and <c>content/filename.ext</c> (both without a leading slash)
    /// as the node's content collection. Returns the original value for absolute URLs, data URIs,
    /// and inline SVG — and null for legacy Fluent icon names.
    ///
    /// <para>🚨 The URL is the ACCESS-CONTROLLED content route
    /// (<see cref="ContentCollectionsExtensions.GetNodeContentFileUrl"/>), not <c>/static</c>. It
    /// used to be <c>/static/storage/content/{nodePath}/{file}</c>, which read the mesh-level
    /// backing store directly and consulted no partition's policy at all — so a private Space's
    /// icon, thumbnail or logo was world-readable to anyone with (or guessing) the URL
    /// (issue #587).</para>
    /// </summary>
    public static string? ResolveContentPath(string? value, string? nodePath)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        // "content:filename.ext" — documented canonical form.
        if (value.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = value["content:".Length..];
            if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(nodePath))
                return ContentCollectionsExtensions.GetNodeContentFileUrl(nodePath, fileName);
        }

        // "content/filename.ext" — natural bare form many users type after browsing the collection.
        if (value.StartsWith("content/", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = value["content/".Length..];
            if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(nodePath))
                return ContentCollectionsExtensions.GetNodeContentFileUrl(nodePath, fileName);
        }

        return GetIconForRendering(value);
    }

    /// <summary>
    /// A Fluent icon name is purely ASCII letters starting with uppercase (e.g., "Chat", "ArrowLeft").
    /// </summary>
    public static bool IsFluentIconName(string value)
        => !string.IsNullOrEmpty(value)
           && char.IsAsciiLetterUpper(value[0])
           && value.All(char.IsAsciiLetter);

    /// <summary>
    /// Returns true if the icon value is an inline SVG string.
    /// </summary>
    public static bool IsInlineSvg(string? icon)
        => !string.IsNullOrEmpty(icon) && icon.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the icon value is a URL or data URI (renderable as img src).
    /// </summary>
    public static bool IsImageUrl(string? icon)
        => !string.IsNullOrEmpty(icon) && (icon.Contains('/') || icon.StartsWith("data:"));

    /// <summary>
    /// Returns true if the icon value is an emoji or other non-URL, non-SVG, non-Fluent text.
    /// </summary>
    public static bool IsEmoji(string? icon)
        => !string.IsNullOrEmpty(icon) && !IsImageUrl(icon) && !IsInlineSvg(icon) && !IsFluentIconName(icon);

    /// <summary>
    /// Ensures an inline <c>&lt;svg&gt;</c> renders at an explicit pixel size when injected
    /// as a raw HTML string (e.g. <c>Controls.Html</c> surfaces, where no scoped CSS can
    /// reach the markup). Node icons are typically authored with a <c>viewBox</c> but NO
    /// <c>width</c>/<c>height</c>; without an intrinsic size such an svg renders at the
    /// browser default (~300×150) and overflows/collapses — a blank tile. Injects a
    /// <c>style</c> attribute right after the opening <c>&lt;svg</c> tag; because a
    /// duplicate attribute's FIRST occurrence wins in HTML parsing, the injected size
    /// takes precedence over any author-supplied inline style.
    /// </summary>
    /// <param name="svg">The inline svg markup (anything before the first <c>&lt;svg</c> is preserved).</param>
    /// <param name="pixels">The square size, in CSS pixels, the svg should occupy.</param>
    public static string SizeInlineSvg(string svg, int pixels)
    {
        if (string.IsNullOrEmpty(svg))
            return svg;
        var idx = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return svg;
        var insertAt = idx + "<svg".Length;
        return svg.Insert(insertAt,
            $" style=\"width: {pixels}px; height: {pixels}px; display: block;\"");
    }

    /// <summary>The one media type that tells a browser "this icon scales losslessly".</summary>
    public const string SvgMediaType = "image/svg+xml";

    /// <summary>
    /// 🚨 THE NODE'S ICON, READY FOR THE BROWSER TAB — a node page identifies itself in the tab
    /// strip rather than wearing the same portal favicon as every other page.
    ///
    /// <para>Resolution is <see cref="ResolveNodeIcon(MeshNode?)"/>, i.e. EXACTLY what the app already renders
    /// for the node in the tree, the menus and its cards (own icon → the shipped glyph of that name
    /// → the NodeType default → a neutral box). Nothing is synthesised beyond putting a value an
    /// <c>href</c> cannot carry into a form it can: an inline <c>&lt;svg&gt;</c> identicon and a text
    /// glyph travel as <c>data:</c> URIs because they are MARKUP, not locations — the same mechanism
    /// the per-instance favicon uses. So the tab and the app can never disagree about what a node
    /// looks like.</para>
    ///
    /// <para>Total, like <see cref="ResolveNodeIcon(MeshNode?)"/>: every node resolves to something, so a tab
    /// never silently keeps the previous page's icon.</para>
    /// </summary>
    /// <param name="node">The node whose page is being shown.</param>
    public static IconLink ResolveIconLink(MeshNode node) => ResolveIconLink(node, null);

    /// <summary>
    /// <see cref="ResolveIconLink(MeshNode)"/> with package-root inheritance — the tab of a page
    /// under a marked package wears the PACKAGE's mark rather than a generic type glyph, which is
    /// the surface issue #2075 was actually about: at 16 px "which package" is the only thing worth
    /// saying, and <c>document</c> says nothing.
    ///
    /// <para>Resolution stays <see cref="ResolveNodeIcon(MeshNode?, MeshNode?)"/> — the identical
    /// chain the app renders — so the tab and the page still cannot disagree, including about an
    /// inherited mark. The official-mark substitution below applies to an inherited value too: a
    /// package whose mark is a vendor's still yields the portal's own mark in the TAB, because a
    /// favicon claims the tab is theirs.</para>
    /// </summary>
    /// <param name="node">The node whose page is being shown.</param>
    /// <param name="partitionRoot">The node's partition root, when the caller has it; null skips
    /// inheritance.</param>
    public static IconLink ResolveIconLink(MeshNode node, MeshNode? partitionRoot)
    {
        var icon = ResolveNodeIcon(node, partitionRoot);

        // 🚨 THE ONE PLACE THE TAB AND THE CARD DELIBERATELY DIVERGE. A card showing a vendor's mark
        // is nominative use — the package is an API client to that service, and the mark says which
        // one. A FAVICON is not: it identifies the site occupying the tab, so a vendor mark there
        // claims the tab IS theirs. The portal's own mark is the honest answer, and it is why
        // IsOfficialMark exists as a query rather than only a render switch.
        return IsOfficialMark(icon)
            ? IconLinkFor(MeshWeaverMarkUrl)
            : IconLinkFor(icon);
    }

    /// <summary>The portal's own mark, shipped as <c>Icons/meshweaver-logo.svg</c>.</summary>
    private const string MeshWeaverMarkIcon = "meshweaver-logo";

    /// <summary>
    /// The URL of <see cref="MeshWeaverMarkIcon"/> — the browser-tab icon used wherever a node's own
    /// icon is an official third-party mark.
    ///
    /// <para>🚨 Built as a URL rather than passed as a NAME on purpose: <see cref="ShippedIconFor"/>
    /// only resolves names <see cref="IsFluentIconName"/> accepts, which requires an uppercase
    /// initial and all-ASCII-letters — so the hyphenated, lower-case <c>meshweaver-logo</c> resolves
    /// to null through that path and the tab would silently fall back to the neutral box. Same
    /// construction as <see cref="NeutralIconUrl"/>.</para>
    /// </summary>
    public static readonly string MeshWeaverMarkUrl = $"/static/NodeTypeIcons/{MeshWeaverMarkIcon}.svg";

    /// <summary>
    /// Whether an icon VALUE is an official third-party mark — inline svg carrying
    /// <see cref="IconBackplate.OfficialMarkAttribute"/>. Any other form (URL, glyph, Fluent name)
    /// cannot make the claim, so it is false for them.
    /// </summary>
    public static bool IsOfficialMark(string? icon) =>
        DomainIcon.Parse(icon) is { Provider: DomainIcon.InlineSvgProvider } parsed
        && IconBackplate.IsOfficialMark(parsed.Id);

    /// <summary>
    /// The <c>&lt;link rel="icon"&gt;</c> form of one icon value, classified the same way the
    /// rendered icon is (<see cref="ResolveRenderable"/>) so the two cannot drift apart.
    /// </summary>
    /// <param name="icon">The icon value — any of the four forms (URL, inline svg, glyph, Fluent name).</param>
    /// <param name="fallback">The icon to fall back to when <paramref name="icon"/> cannot be
    /// rendered; the neutral box glyph when omitted.</param>
    public static IconLink IconLinkFor(string? icon, string? fallback = null)
    {
        var renderable = ResolveRenderable(icon, fallback);
        return renderable.Kind switch
        {
            IconRenderKind.InlineSvg => new IconLink(SvgDataUri(renderable.Value), SvgMediaType),
            IconRenderKind.Glyph => new IconLink(SvgDataUri(GlyphSvg(renderable.Value)), SvgMediaType),
            _ => new IconLink(renderable.Value, MediaTypeOfUrl(renderable.Value)),
        };
    }

    /// <summary>Inline <c>&lt;svg&gt;</c> markup as a <c>data:</c> URI — percent-encoded rather than
    /// base64 so the markup stays readable in view-source and the URI stays short.</summary>
    public static string SvgDataUri(string svg) =>
        "data:" + SvgMediaType + "," + Uri.EscapeDataString(svg);

    /// <summary>
    /// A text glyph (an emoji) wrapped in an SVG that draws it, because no <c>href</c> can carry a
    /// character. Only the FIRST grapheme cluster is drawn — a multi-codepoint emoji is one cluster,
    /// while an icon field holding a whole word would otherwise overflow the canvas.
    /// </summary>
    private static string GlyphSvg(string glyph)
    {
        var cluster = glyph.Length == 0
            ? glyph
            : glyph[..StringInfo.GetNextTextElementLength(glyph)];
        // The glyph sits on a generated plate (hue stable per glyph) for the same reason inline
        // svg does: a favicon renders on whatever the browser's tab strip paints, and the plate is
        // what keeps it legible there. Text is white for the rare monogram; an emoji ignores fill.
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\">"
               + "<rect width=\"32\" height=\"32\" rx=\"7\" fill=\"" + IconBackplate.HueFor(cluster) + "\"/>"
               + "<text x=\"16\" y=\"17\" text-anchor=\"middle\" dominant-baseline=\"central\" "
               + "font-size=\"24\" fill=\"#fff\">" + XmlEscape(cluster) + "</text></svg>";
    }

    /// <summary>Escapes the five XML markup characters so a glyph like <c>R&amp;D</c> still yields
    /// well-formed SVG.</summary>
    private static string XmlEscape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    /// <summary>
    /// The media type an icon URL pins down by itself, or null when it does not. A content-route URL
    /// carries the file's extension, so this covers the authored cases; anything unrecognised is
    /// declared as nothing rather than guessed at.
    /// </summary>
    private static string? MediaTypeOfUrl(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var mime = url["data:".Length..];
            var end = mime.IndexOfAny([';', ',']);
            mime = end < 0 ? mime : mime[..end];
            return mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? mime : null;
        }

        // A query string is legitimate on a content URL, so match the extension before it — and only
        // within the last SEGMENT, or a dotted directory ("ACME/My.Space/content/logo") would read as
        // an extension and get a made-up media type.
        var path = url.Split('?')[0];
        var file = path[(path.LastIndexOf('/') + 1)..];
        var dot = file.LastIndexOf('.');
        return dot < 0 ? null : file[(dot + 1)..].ToLowerInvariant() switch
        {
            "svg" => SvgMediaType,
            "png" => "image/png",
            "ico" => "image/x-icon",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            _ => null,
        };
    }

    /// <summary>
    /// Legacy method — returns the icon only if it's an image URL.
    /// Prefer <see cref="GetIconForRendering"/> which also returns SVG and emoji icons.
    /// </summary>
    [Obsolete("Use GetIconForRendering instead")]
    public static string? GetIconAsImageUrl(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
            return null;
        return icon.Contains('/') || icon.StartsWith("data:") ? icon : null;
    }
}
