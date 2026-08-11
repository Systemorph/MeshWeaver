using System.Net;
using System.Text.RegularExpressions;

namespace MeshWeaver.Graph;

/// <summary>
/// Pure parser of a page's Open Graph head metadata. No I/O — it reads the HTML string a caller
/// already fetched (see <see cref="OpenGraphPreviewService"/>) and extracts the
/// <c>og:*</c> meta tags with their standard fallbacks (<c>&lt;title&gt;</c>,
/// <c>&lt;meta name="description"&gt;</c>). Attribute order inside a tag is free
/// (<c>property</c> before or after <c>content</c>), values are HTML-entity decoded, and a
/// relative <c>og:image</c> is resolved against the page URL.
/// </summary>
public static partial class OpenGraphHtmlParser
{
    // The og tags live in <head>; parsing stops there so a huge body is never scanned.
    // When no </head> is present (fragment, malformed page) a bounded prefix is used.
    private const int MaxScanLength = 262_144;

    [GeneratedRegex("<meta\\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaTag();

    [GeneratedRegex("(?:property|name)\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex KeyAttribute();

    [GeneratedRegex("content\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ContentAttribute();

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    /// <summary>
    /// Parses the Open Graph preview out of <paramref name="html"/>.
    /// </summary>
    /// <param name="url">The page URL the HTML was fetched from — kept on the preview as the
    /// navigation target and used to resolve a relative <c>og:image</c>.</param>
    /// <param name="html">The page HTML (any prefix containing the head suffices).</param>
    /// <returns>The parsed preview; fields the page does not declare are null.</returns>
    public static OpenGraphPreview Parse(string url, string html)
    {
        var head = HeadOf(html);

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
            AbsoluteImage(url, ogImage),
            NullIfEmpty(ogSiteName),
            Fetched: true);
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

    /// <summary>Resolves the declared image to an absolute URL against the page URL. Already
    /// absolute → unchanged; relative and resolvable → combined; otherwise null (a bare
    /// unresolvable name would break the card's <c>&lt;img&gt;</c>).</summary>
    private static string? AbsoluteImage(string pageUrl, string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;
        if (Uri.TryCreate(image, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return image;
        return Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)
               && Uri.TryCreate(baseUri, image, out var resolved)
            ? resolved.ToString()
            : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
