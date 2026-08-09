using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.Markdown.Export.Pixel;

/// <summary>
/// Composes a deck's slides into ONE self-contained HTML document for the headless browser to
/// print — the pixel-faithful counterpart to <c>DocumentBuilder</c> + <c>PdfDocumentRenderer</c>.
///
/// <para><b>Everything visual is borrowed, nothing is re-invented.</b> The slide body comes from
/// the framework's own markdown pipeline (<c>MarkdownViewLogic.Render</c> → the cached
/// <c>MarkdownExtensions.CreateMarkdownPipeline</c>) — the same renderer the portal uses, so raw
/// HTML and inline SVG pass through exactly as they do on screen. The stage CSS comes from
/// <see cref="SlideLayoutAreas.ThemeTokens"/> and the sibling <c>SlidePrint.css</c>, mirroring
/// <c>SlideLayoutAreas.BuildStage</c>. The document skeleton and the per-slide section are
/// <b>template files</b> (<c>SlidePrint.html</c> / <c>SlidePrintSection.html</c>) with named
/// placeholders — this class interpolates content into templates, it never builds markup.</para>
///
/// <para>Pure and synchronous by design: no hub, no IO, no browser. That is what makes the whole
/// fidelity story testable without installing anything.</para>
/// </summary>
public static partial class SlidePrintComposer
{
    private const string TitleToken = "{{TITLE}}";
    private const string StylesToken = "{{STYLES}}";
    private const string SlidesToken = "{{SLIDES}}";
    private const string ThemeTokensToken = "{{THEME_TOKENS}}";
    private const string BackgroundToken = "{{BACKGROUND}}";
    private const string BodyToken = "{{BODY}}";

    /// <summary>
    /// The stage background used when a slide sets no <see cref="SlideContent.Background"/> —
    /// the same <c>var(--ae-bg)</c> default the live stage falls back to.
    /// </summary>
    public const string DefaultBackground = "var(--ae-bg)";

    private const string EmptySlideHint =
        "*This slide has no content yet. Edit the node's `Content` to fill the stage.*";

    /// <summary>
    /// Composes the print document. <paramref name="slides"/> is already in the deck's own order
    /// (resolved by <c>DeckLayoutAreas.ResolveDeckSelection</c> — one source of truth for order,
    /// shared with the live views and the content-faithful export).
    /// </summary>
    /// <param name="title">Document title; lands in <c>&lt;title&gt;</c> (HTML-encoded).</param>
    /// <param name="slides">The slides to print, in order.</param>
    /// <returns>A complete, self-contained HTML document.</returns>
    public static string Compose(string title, IEnumerable<PrintSlide> slides)
    {
        var sections = new StringBuilder();
        foreach (var slide in slides)
            sections.Append(ComposeSection(slide));

        return SlidePrintTemplates.Document
            .Replace(TitleToken, WebUtility.HtmlEncode(title))
            .Replace(StylesToken, SlidePrintTemplates.Styles.Replace(ThemeTokensToken, SlideLayoutAreas.ThemeTokens))
            .Replace(SlidesToken, sections.ToString());
    }

    private static string ComposeSection(PrintSlide slide)
    {
        var background = string.IsNullOrWhiteSpace(slide.Content?.Background)
            ? DefaultBackground
            : slide.Content!.Background!;

        var markdown = string.IsNullOrWhiteSpace(slide.Content?.Content)
            ? EmptySlideHint
            : slide.Content!.Content!;

        // The framework's markdown pipeline — raw HTML and inline SVG pass through verbatim,
        // which is precisely the fidelity the document-model renderer cannot reproduce.
        var body = MarkdownViewLogic.Render(markdown, slide.Collection, slide.NodePath).Html;

        return SlidePrintTemplates.Section
            // The background is a CSS value going into an attribute: encode it so a quote in
            // author content cannot break out of the attribute. Same trust model as the live
            // stage, which also inlines this value into a style attribute.
            .Replace(BackgroundToken, WebUtility.HtmlEncode(background))
            .Replace(BodyToken, body);
    }

    /// <summary>
    /// Finds every content-collection asset the composed document references — the
    /// <c>api/content/{collection}/{path}</c> hrefs the markdown pipeline rewrites image sources
    /// onto, plus the same hrefs appearing inside CSS <c>url(...)</c> (a slide's
    /// <c>Background: url(api/content/…)</c> image).
    ///
    /// <para>Those are ACCESS-CONTROLLED portal routes; a <c>file://</c> document cannot fetch
    /// them. The caller resolves each one under the exporting user's identity and hands the bytes
    /// back through <see cref="InlineAssets"/>, so the printed deck carries its images instead of
    /// linking to them.</para>
    /// </summary>
    public static ImmutableArray<string> CollectAssetReferences(string html) =>
        AssetReferenceRegex()
            .Matches(html)
            .Select(m => m.Groups["ref"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

    /// <summary>
    /// Replaces every reference from <see cref="CollectAssetReferences"/> that the caller could
    /// resolve with its <c>data:</c> URI, making the document self-contained. References the
    /// caller could not resolve are left untouched — a missing image prints as a broken image,
    /// exactly as it would on screen, rather than failing the whole export.
    /// </summary>
    /// <param name="html">The composed document.</param>
    /// <param name="dataUris">Map of asset reference → <c>data:</c> URI.</param>
    public static string InlineAssets(string html, IReadOnlyDictionary<string, string> dataUris)
    {
        if (dataUris.Count == 0)
            return html;

        var result = html;
        foreach (var (reference, dataUri) in dataUris)
            result = result.Replace(reference, dataUri, StringComparison.Ordinal);
        return result;
    }

    /// <summary>
    /// Builds the <c>data:</c> URI that replaces an asset reference, picking the media type from
    /// the file extension. Immutable lookup — a constant, not a cache.
    /// </summary>
    public static string ToDataUri(string path, byte[] bytes) =>
        $"data:{MediaTypeFor(path)};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>Media type for an asset path; <c>application/octet-stream</c> when unknown.</summary>
    public static string MediaTypeFor(string path) =>
        MediaTypes.TryGetValue(Path.GetExtension(path), out var mediaType)
            ? mediaType
            : "application/octet-stream";

    private static readonly ImmutableDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".avif"] = "image/avif",
            [".bmp"] = "image/bmp",
            [".svg"] = "image/svg+xml",
            [".ico"] = "image/x-icon",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".otf"] = "font/otf",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Strips the content-route prefix off an asset reference and percent-decodes it, yielding the
    /// <c>{node}/{collection}/{file}</c>-or-<c>{node}/{file…}</c> form
    /// <see cref="ContentFileResolver.Resolve"/> takes — or null when the reference is not a
    /// content-collection route at all.
    ///
    /// <para><b>It does NOT split off a collection, deliberately.</b> Which segments name the node
    /// and which name the collection cannot be read off the string: the markdown pipeline writes
    /// <c>api/content/{nodePath}/{file}</c>, where the first segment is the node's PARTITION rather
    /// than a collection, and a node path is any number of segments long. Only the resolver — which
    /// resolves the owning node and asks it which collections it has — can tell the two shapes
    /// apart. Splitting here instead is exactly how a stored slide image came to fail silently
    /// (issue #990): the split asked the content service for a collection named after the
    /// partition, which does not exist, and the CSP turned the un-inlined reference into a blank
    /// image.</para>
    ///
    /// <para>Percent-escapes are undone <b>per segment</b>
    /// (<see cref="ContentCollectionsExtensions.DecodeCollectionPath"/>), never across the whole
    /// string — the same decode the content route applies, and for the same reason: a top-level
    /// segment IS a partition and the router lowercases it. Per-segment decoding cannot introduce
    /// or remove a <c>/</c>, so it can never split, merge or rename a segment, and therefore cannot
    /// create or mask a partition collision. The <c>~</c> form of a qualified collection name
    /// (<see cref="ContentCollectionsExtensions.EncodeCollectionName"/>) is left for the resolver,
    /// which decodes it on the one segment that can be a collection.</para>
    /// </summary>
    /// <param name="reference">An asset reference as it appears in the composed document.</param>
    /// <returns>The decoded, prefix-less reference, or <c>null</c>.</returns>
    public static string? ToContentReference(string reference)
    {
        if (!AssetReferenceRegex().IsMatch(reference))
            return null;

        // References are captured from markup and are site-relative (no leading slash); the route
        // prefix is rooted.
        var rooted = reference.Replace('\\', '/');
        if (!rooted.StartsWith('/'))
            rooted = "/" + rooted;

        var prefix = ContentCollectionsExtensions.ContentFileRoutePrefix + "/";
        if (!rooted.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = ContentCollectionsExtensions
            .DecodeCollectionPath(rooted[prefix.Length..])
            .TrimStart('/');

        return string.IsNullOrEmpty(relative) ? null : relative;
    }

    // api/content/{collection}/{path} — optionally leading '/', stopping at a quote, whitespace,
    // ')' (CSS url(...)) or '#'/'?'. Mirrors MarkdownExtensions.ToContentHref's shape.
    [GeneratedRegex(
        @"(?<ref>/?api/content/(?<collection>[^/""'\s)]+)/(?<path>[^""'\s)#?]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetReferenceRegex();
}

/// <summary>
/// One slide to print: its content plus the two bits of context the markdown pipeline needs to
/// resolve relative image hrefs and links the same way the live view does.
/// </summary>
/// <param name="Content">The slide's content, or null for an empty slide.</param>
/// <param name="NodePath">The slide's mesh path — resolves relative links.</param>
/// <param name="Collection">The content collection relative image paths resolve against.</param>
public record PrintSlide(SlideContent? Content, string? NodePath = null, object? Collection = null);
