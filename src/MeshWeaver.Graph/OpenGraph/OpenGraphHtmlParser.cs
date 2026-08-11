using System.Net;
using System.Text.RegularExpressions;

namespace MeshWeaver.Graph;

/// <summary>
/// Pure parser of a page's Open Graph head metadata. No I/O — it reads the HTML string a caller
/// already fetched (see <see cref="OpenGraphPreviewService"/>) and extracts the
/// <c>og:*</c> meta tags with their standard fallbacks (<c>&lt;title&gt;</c>,
/// <c>&lt;meta name="description"&gt;</c>) plus the page's declared ICON
/// (<c>&lt;link rel="icon"&gt;</c> / <c>apple-touch-icon</c>). Attribute order inside a tag is
/// free (<c>property</c> before or after <c>content</c>), values are HTML-entity decoded, and
/// relative URLs are resolved against the page's effective base.
///
/// <para>🚨 <b>Relative URLs resolve against <c>&lt;base href&gt;</c>, not the page URL.</b> Every
/// Blazor portal — MeshWeaver's included — serves <c>&lt;base href="/"&gt;</c> together with a
/// RELATIVE <c>&lt;link rel="icon" href="favicon.ico"&gt;</c>. Resolving that against the page URL
/// gives <c>/Some/Section/favicon.ico</c> for any nested page, which a catch-all-routed SPA
/// answers with <b>200 text/html</b> rather than a 404 — a silently broken <c>&lt;img&gt;</c> that
/// looks like a working link. Honouring the base tag is what makes the icon resolve to the real
/// <c>/favicon.ico</c>.</para>
/// </summary>
public static partial class OpenGraphHtmlParser
{
    // The og tags live in <head>; parsing stops there so a huge body is never scanned.
    // When no </head> is present (fragment, malformed page) a bounded prefix is used.
    private const int MaxScanLength = 262_144;

    /// <summary>Effective pixel size credited to an icon that scales losslessly to any box —
    /// an SVG, or one declaring <c>sizes="any"</c>. Ranks above every raster candidate.</summary>
    private const int ScalableIconSize = 1024;

    /// <summary>Effective size assumed for an <c>apple-touch-icon</c> that declares no
    /// <c>sizes</c>: the de-facto convention is 180×180, purpose-built as a square tile — which is
    /// exactly the card's icon box, so it outranks a sizeless favicon.</summary>
    private const int DefaultAppleTouchIconSize = 180;

    /// <summary>Effective size assumed for a <c>rel="icon"</c> that declares no <c>sizes</c>
    /// (a classic <c>favicon.ico</c>, conventionally 16–32 px).</summary>
    private const int DefaultIconSize = 32;

    [GeneratedRegex("<meta\\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaTag();

    [GeneratedRegex("(?:property|name)\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex KeyAttribute();

    [GeneratedRegex("content\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ContentAttribute();

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex("<link\\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkTag();

    [GeneratedRegex("<base\\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BaseTag();

    // The (?<![-\w:]) lookbehind pins each name to a REAL attribute: without it `href` also
    // matches inside `data-href` / `xlink:href`, and `type` inside `data-type`.
    [GeneratedRegex("(?<![-\\w:])rel\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RelAttribute();

    [GeneratedRegex("(?<![-\\w:])href\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HrefAttribute();

    [GeneratedRegex("(?<![-\\w:])sizes\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SizesAttribute();

    [GeneratedRegex("(?<![-\\w:])type\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TypeAttribute();

    [GeneratedRegex("(\\d+)\\s*[xX]\\s*(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SizePair();

    /// <summary>
    /// Parses the Open Graph preview out of <paramref name="html"/>.
    /// </summary>
    /// <param name="url">The page URL the HTML was fetched from — kept on the preview as the
    /// navigation target and used (with any <c>&lt;base href&gt;</c>) to resolve relative
    /// URLs.</param>
    /// <param name="html">The page HTML (any prefix containing the head suffices).</param>
    /// <returns>The parsed preview; fields the page does not declare are null.</returns>
    public static OpenGraphPreview Parse(string url, string html)
    {
        var head = HeadOf(html);
        var baseUri = EffectiveBase(url, head);

        string? ogTitle = null, ogDescription = null, ogImage = null, ogSiteName = null,
            metaDescription = null;

        foreach (Match tag in MetaTag().Matches(head))
        {
            var key = KeyAttribute().Match(tag.Value);
            var content = ContentAttribute().Match(tag.Value);
            if (!key.Success || !content.Success)
                continue;
            var value = WebUtility.HtmlDecode(content.Groups[2].Value).Trim();
            if (value.Length == 0)
                continue;
            switch (key.Groups[2].Value.ToLowerInvariant())
            {
                // First occurrence wins, matching how unfurl bots read the tags.
                case "og:title": ogTitle ??= value; break;
                case "og:description": ogDescription ??= value; break;
                case "og:image": ogImage ??= value; break;
                case "og:site_name": ogSiteName ??= value; break;
                case "description": metaDescription ??= value; break;
            }
        }

        var titleTag = TitleTag().Match(head);
        var title = ogTitle
            ?? (titleTag.Success ? WebUtility.HtmlDecode(titleTag.Groups[1].Value).Trim() : null);

        return new OpenGraphPreview(
            url,
            NullIfEmpty(title),
            NullIfEmpty(ogDescription ?? metaDescription),
            AbsoluteUrl(baseUri, ogImage),
            NullIfEmpty(ogSiteName),
            Fetched: true,
            Icon: AbsoluteUrl(baseUri, SelectIcon(head)),
            // The page declared Open Graph proper — not merely a <title> that every HTML
            // document has, including an SPA catch-all shell. Gates the preview cache; see
            // OpenGraphPreview.IsResolved.
            DeclaresOpenGraph: ogTitle is not null);
    }

    /// <summary>The head slice of the document: everything up to <c>&lt;/head&gt;</c>, else a
    /// bounded prefix so a malformed page cannot make the regex scan megabytes of body.</summary>
    private static string HeadOf(string html)
    {
        var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd >= 0)
            return html[..headEnd];
        return html.Length <= MaxScanLength ? html : html[..MaxScanLength];
    }

    /// <summary>
    /// The base every relative URL on the page resolves against: the page's own
    /// <c>&lt;base href&gt;</c> when it declares one (itself resolved against the page URL, so a
    /// relative base works), else the page URL. Null only when the page URL is not absolute.
    /// </summary>
    private static Uri? EffectiveBase(string pageUrl, string head)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
            return null;

        var baseTag = BaseTag().Match(head);
        if (!baseTag.Success)
            return page;

        var href = HrefAttribute().Match(baseTag.Value);
        if (!href.Success)
            return page;

        var value = WebUtility.HtmlDecode(href.Groups[2].Value).Trim();
        return value.Length > 0 && Uri.TryCreate(page, value, out var resolved) ? resolved : page;
    }

    /// <summary>
    /// The best icon the page declares, or null when it declares none.
    ///
    /// <para>Candidates are every <c>&lt;link&gt;</c> whose <c>rel</c> carries the <c>icon</c>
    /// token (<c>icon</c>, <c>shortcut icon</c>) or is an <c>apple-touch-icon</c>. They are ranked
    /// on ONE axis — effective pixel size — so the winner is simply the icon that renders sharpest
    /// in the card's 48 px box: a scalable SVG beats every raster, a declared <c>sizes</c> is
    /// taken at face value, and a sizeless <c>apple-touch-icon</c> (conventionally 180 px) beats a
    /// sizeless <c>favicon.ico</c> (conventionally 16–32 px).</para>
    ///
    /// <para>🚨 <b>A tie is broken by the LAST declaration, as browsers break it.</b> Site chrome
    /// emits its favicon early (in the shared layout); a PAGE that declares an icon of its own
    /// emits it later, in the per-page head. Preferring the first would mean a page could never
    /// override the site-wide mark with its own — every card in a grid of pages would then draw the
    /// same portal logo, which is exactly the defect this rule exists to prevent.</para>
    ///
    /// <para><c>rel="mask-icon"</c> is deliberately NOT a candidate: it is a monochrome Safari
    /// pinned-tab silhouette, not the site's icon.</para>
    /// </summary>
    private static string? SelectIcon(string head)
    {
        string? best = null;
        var bestScore = int.MinValue;

        foreach (Match tag in LinkTag().Matches(head))
        {
            var rel = RelAttribute().Match(tag.Value);
            var href = HrefAttribute().Match(tag.Value);
            if (!rel.Success || !href.Success)
                continue;

            var value = WebUtility.HtmlDecode(href.Groups[2].Value).Trim();
            if (value.Length == 0)
                continue;

            var score = IconScore(rel.Groups[2].Value, tag.Value);
            // `<` not `<=`: an equally-ranked LATER declaration wins, so a page's own icon
            // overrides the site-wide favicon its layout emitted earlier.
            if (score is null || score.Value < bestScore)
                continue;

            bestScore = score.Value;
            best = value;
        }

        return best;
    }

    /// <summary>The effective pixel size of one <c>&lt;link&gt;</c> candidate, or null when the
    /// tag is not an icon at all.</summary>
    private static int? IconScore(string rel, string tag)
    {
        var isAppleTouch = false;
        var isIcon = false;
        foreach (var token in rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("icon", StringComparison.OrdinalIgnoreCase))
                isIcon = true;
            else if (token.StartsWith("apple-touch-icon", StringComparison.OrdinalIgnoreCase))
                isAppleTouch = true;
        }

        if (!isIcon && !isAppleTouch)
            return null;

        var type = TypeAttribute().Match(tag);
        if (type.Success && type.Groups[2].Value.Contains("svg", StringComparison.OrdinalIgnoreCase))
            return ScalableIconSize;

        var sizes = SizesAttribute().Match(tag);
        if (sizes.Success)
        {
            var declared = LargestSize(sizes.Groups[2].Value);
            if (declared is not null)
                return declared;
        }

        return isAppleTouch ? DefaultAppleTouchIconSize : DefaultIconSize;
    }

    /// <summary>The largest dimension declared in a <c>sizes</c> attribute
    /// (<c>"16x16 32x32"</c> → 32), or <see cref="ScalableIconSize"/> for <c>"any"</c>. Null when
    /// the attribute declares nothing parseable.</summary>
    private static int? LargestSize(string sizes)
    {
        if (sizes.Contains("any", StringComparison.OrdinalIgnoreCase))
            return ScalableIconSize;

        int? largest = null;
        foreach (Match pair in SizePair().Matches(sizes))
        {
            if (!int.TryParse(pair.Groups[1].Value, out var width))
                continue;
            largest = largest is null ? width : Math.Max(largest.Value, width);
        }

        return largest;
    }

    /// <summary>Resolves a declared URL to an absolute one against the page's effective base.
    /// Already absolute http(s) → unchanged; a <c>data:</c> URI (a common inline-SVG icon) is
    /// markup, returned verbatim; relative and resolvable → combined; otherwise null (a bare
    /// unresolvable name would break the card's <c>&lt;img&gt;</c>).</summary>
    private static string? AbsoluteUrl(Uri? baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return value;
        return baseUri is not null && Uri.TryCreate(baseUri, value, out var resolved)
            ? resolved.ToString()
            : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
