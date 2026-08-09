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
    /// Splits an asset reference into the collection name and collection-relative path a
    /// server-side read needs, or null when it is not a content-collection route.
    ///
    /// <para>Normalisation goes through the framework's own reader,
    /// <see cref="ContentPathReference.TryGetRelativePath"/> — which also folds out the
    /// default-collection segment of <c>/api/content/{collection}/content/{path}</c> — and the
    /// first-segment-names-the-collection split is the same one <c>BrandingResolver</c> uses for
    /// exactly this job. One convention for reading content server-side, not two.</para>
    ///
    /// <para><b>Both segments are decoded, each with the decoder that matches how it was
    /// encoded.</b> A collection name carries <c>/</c> as <c>~</c>
    /// (<see cref="ContentCollectionsExtensions.EncodeCollectionName"/>), so leaving it encoded
    /// asks the content service for a collection that does not exist and the asset silently stays
    /// broken. Percent-escapes are then undone <b>per segment</b>
    /// (<see cref="ContentCollectionsExtensions.DecodeCollectionPath"/>), never across the whole
    /// string: that is deliberate, because a top-level segment IS a partition and the router
    /// lowercases it. Per-segment decoding cannot introduce or remove a <c>/</c>, so it can never
    /// split, merge or rename a segment — and therefore cannot create or mask a partition
    /// collision. Nothing here case-folds; the value is passed through exactly as authored.</para>
    /// </summary>
    public static (string Collection, string Path)? ParseAssetReference(string reference)
    {
        if (!AssetReferenceRegex().IsMatch(reference))
            return null;

        // TryGetRelativePath matches on the rooted route prefix; our references are captured from
        // markup and may be site-relative.
        var rooted = reference.StartsWith('/') ? reference : "/" + reference;
        var relative = ContentPathReference.TryGetRelativePath(rooted);
        if (string.IsNullOrEmpty(relative))
            return null;

        relative = relative.Replace('\\', '/').TrimStart('/');
        var slash = relative.IndexOf('/');
        if (slash <= 0 || slash == relative.Length - 1)
            return null;

        var collection = ContentCollectionsExtensions.DecodeCollectionPath(
            ContentCollectionsExtensions.DecodeCollectionName(relative[..slash]));
        var path = ContentCollectionsExtensions.DecodeCollectionPath(relative[(slash + 1)..]);

        return string.IsNullOrEmpty(collection) || string.IsNullOrEmpty(path)
            ? null
            : (collection, path);
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
