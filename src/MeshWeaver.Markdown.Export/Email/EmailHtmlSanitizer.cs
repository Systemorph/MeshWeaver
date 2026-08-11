using System.Collections.Immutable;
using HtmlAgilityPack;

namespace MeshWeaver.Markdown.Export.Email;

/// <summary>
/// Reduces rendered portal HTML to what a mail client can actually display. Operates on a real
/// DOM (never regex over markup) so a stripped element takes its whole subtree with it and
/// nothing can be half-removed.
///
/// <para>Everything removed here is removed because it either does not work or actively breaks
/// in mail — see the per-rule comments. Nothing is removed for taste.</para>
/// </summary>
public static class EmailHtmlSanitizer
{
    /// <summary>
    /// Elements deleted with their subtree.
    ///
    /// <para><c>svg</c> is the one that bites hardest: Outlook on Windows renders through the WORD
    /// engine, which has no SVG support at all and paints a broken-image box in its place. The
    /// portal draws node icons as INLINE SVG in <c>currentColor</c>, so a document full of node
    /// links would arrive full of broken boxes. Dropping the icon entirely is strictly better than
    /// shipping a broken one; an icon that must survive has to be a hosted raster URL.</para>
    ///
    /// <para><c>script</c>/<c>noscript</c>: no mail client executes JS, and many quarantine a
    /// message that contains any. <c>style</c>/<c>link</c>: head styles are stripped or rewritten
    /// by Gmail/Outlook.com, so styling has to be inline; leaving a dead stylesheet reference
    /// behind only risks a "download blocked content" banner. <c>iframe</c>/<c>object</c>/
    /// <c>embed</c>/<c>form</c>/<c>input</c>/<c>button</c>: blocked or inert everywhere.</para>
    /// </summary>
    private static readonly ImmutableHashSet<string> StrippedElements =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "script", "noscript", "style", "link", "svg", "iframe", "object", "embed",
            "form", "input", "button", "template");

    /// <summary>
    /// Attributes deleted wherever they appear. <c>class</c>/<c>id</c> address a stylesheet that
    /// does not travel with the mail, so they are dead weight; every <c>on*</c> handler is script.
    /// </summary>
    private static readonly ImmutableHashSet<string> StrippedAttributes =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "class", "id", "srcset", "loading");

    /// <summary>
    /// Strips unsupported markup from <paramref name="node"/>'s subtree and rewrites every
    /// relative <c>href</c>/<c>src</c> to an absolute URL against
    /// <see cref="EmailHtmlOptions.NormalizedBaseUrl"/>.
    ///
    /// <para>Absolutising is not cosmetic: a mail client has no page origin, so a relative
    /// <c>/Some/Node</c> link is simply dead when clicked out of the inbox.</para>
    /// </summary>
    public static void Sanitize(HtmlNode node, EmailHtmlOptions options)
    {
        foreach (var stripped in node
                     .Descendants()
                     .Where(d => StrippedElements.Contains(d.Name))
                     // Materialise before mutating — removing during a lazy DOM walk skips siblings.
                     .ToList())
            stripped.Remove();

        foreach (var element in node.Descendants().Where(d => d.NodeType == HtmlNodeType.Element).ToList())
        {
            foreach (var attribute in element.Attributes.ToList())
            {
                if (StrippedAttributes.Contains(attribute.Name)
                    || attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attribute.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
                {
                    element.Attributes.Remove(attribute);
                    continue;
                }

                if (attribute.Name is "href" or "src")
                    // 🚨 SetAttributeValue, never `attribute.Value = …`. HtmlAgilityPack caches a
                    // node's rendered markup and a direct value assignment does NOT invalidate
                    // that cache, so the rewrite is applied to the DOM but the serialized output
                    // still carries the ORIGINAL url — a relative link that is dead in an inbox,
                    // with nothing in the DOM to show for it.
                    element.SetAttributeValue(attribute.Name, Absolutize(attribute.Value, options.NormalizedBaseUrl));
            }
        }
    }

    /// <summary>
    /// Makes one URL absolute against <paramref name="baseUrl"/>. Already-absolute URLs (any
    /// scheme), protocol-relative URLs, fragments, <c>mailto:</c> and <c>data:</c> pass through
    /// untouched.
    /// </summary>
    public static string Absolutize(string? url, string baseUrl)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.Length == 0)
            return value;

        // A fragment-only link points inside the mail body itself; rewriting it would break it.
        if (value.StartsWith('#'))
            return value;

        if (value.StartsWith("//", StringComparison.Ordinal))
            return "https:" + value;

        // 🚨 Detect the scheme by INSPECTION, never with Uri.TryCreate(…, UriKind.Absolute).
        // On Unix, .NET parses a root-relative path ("/Some/Page") as an implicit absolute
        // `file:` URI, so TryCreate reports it as already-absolute and the link is shipped
        // relative — dead in an inbox, and invisible on a Windows dev machine because there the
        // very same call returns false.
        if (HasScheme(value))
            return value;

        if (string.IsNullOrEmpty(baseUrl))
            return value;

        return value.StartsWith('/')
            ? baseUrl + value
            : baseUrl + "/" + value;
    }

    /// <summary>
    /// True when the value opens with a real URI scheme (<c>https:</c>, <c>mailto:</c>,
    /// <c>data:</c>, <c>cid:</c>, …): letters/digits/<c>+-.</c> terminated by a colon that comes
    /// before any slash. The slash rule is what keeps a relative path whose text happens to
    /// contain a colon (<c>Notes/Q3:draft</c>) from being mistaken for one.
    /// </summary>
    internal static bool HasScheme(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == ':')
                return i > 0;
            if (c == '/' || c == '?' || c == '#')
                return false;
            var isSchemeChar = char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';
            if (!isSchemeChar)
                return false;
            // A scheme must START with a letter; a leading digit is not one.
            if (i == 0 && !char.IsAsciiLetter(c))
                return false;
        }
        return false;
    }
}
