using System.Collections.Immutable;
using System.Text;
using System.Xml;

namespace MeshWeaver.Mesh;

/// <summary>
/// The write-boundary rule for <see cref="MeshNode.Icon"/>: which values an icon may take, judged by
/// what the value IS rather than by what it does not look like.
///
/// <para><b>Why a rule at all.</b> An icon is a free string that one user sets and every other user
/// is shown. The renderers draw an inline-SVG icon as an inert image (<c>Doc/GUI/DisplayControls</c>
/// → "Inline SVG icons render as images"), and that is the protection. This is the second line:
/// a value that could only ever be active content is refused when it is WRITTEN, so it never sits in
/// the store waiting for a renderer that forgets the first line.</para>
///
/// <para><b>What is accepted</b> — everything the platform's own content uses, measured over the
/// core, plugin and satellite node trees before this rule was written:</para>
/// <list type="bullet">
///   <item>nothing (null or blank);</item>
///   <item>a Fluent icon name or an emoji / short text — no markup characters, no scheme;</item>
///   <item>a URL: <c>http(s)://…</c>, a relative or root-relative path, or a <c>content:</c>
///     reference;</item>
///   <item>a raster data URI — <c>data:image/(png|jpeg|gif|webp);base64,…</c>;</item>
///   <item>an SVG — inline, or as a <c>data:image/svg+xml</c> URI — that parses as XML and holds only
///     drawing elements and presentation attributes from an allow-list: no script, no
///     <c>foreignObject</c>, no animation, no event-handler attribute, no reference that leaves the
///     document (<c>href</c> and <c>url(…)</c> must be <c>#fragment</c>s), no processing
///     instruction and no DTD.</item>
/// </list>
///
/// <para>🚨 <b>Parsed, never pattern-matched.</b> The SVG is read with <see cref="XmlReader"/>
/// (DTD prohibited, no resolver), so what is judged is the element tree the browser would build —
/// not a string a regex was taught to recognise. Anything the parser refuses is refused.</para>
///
/// <para><b>Write-only.</b> Nothing here runs on a read: a stored icon that predates the rule still
/// loads (and still renders inert). An update that leaves the icon as it is passes too, so such a
/// node stays editable.</para>
/// </summary>
public static class IconSafety
{
    /// <summary>The SVG namespace.</summary>
    public const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>The XLink namespace (legacy <c>xlink:href</c>).</summary>
    public const string XlinkNamespace = "http://www.w3.org/1999/xlink";

    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>Longest text / emoji icon accepted. The longest in the measured corpus is 15.</summary>
    public const int MaxTextLength = 64;

    /// <summary>
    /// Upper bound on a whole icon value, in characters — checked FIRST, so every later rule runs
    /// over a bounded input. The largest icon in the measured content trees is under 10,000.
    /// </summary>
    public const int MaxIconLength = 256 * 1024;

    /// <summary>Upper bound on an SVG document the parser will read, in characters.</summary>
    public const int MaxSvgCharacters = MaxIconLength;

    /// <summary>Upper bound on one SVG attribute value, in characters.</summary>
    public const int MaxAttributeLength = 64 * 1024;

    // Constant lookups, written once and never at runtime (AGENTS.md → "No static collections").
    private static readonly ImmutableHashSet<string> AllowedElements = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        // No title / desc: HTML treats them as integration points, so their content can be
        // re-parsed as HTML. An icon's accessible name is the alt text of the image it renders as.
        "svg", "g", "defs", "symbol", "use",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textPath",
        "linearGradient", "radialGradient", "stop", "pattern",
        "clipPath", "mask", "marker");

    private static readonly ImmutableHashSet<string> AllowedAttributes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        // No `style`: CSS has URL-bearing functions besides url( (image-set, …), so a declaration
        // block is refused outright rather than parsed. Presentation attributes carry the same
        // properties under the strict value grammar below.
        "id", "class", "version", "role", "focusable", "lang",
        "viewBox", "preserveAspectRatio", "width", "height",
        "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "fx", "fy", "fr",
        "d", "points", "pathLength", "transform",
        "fill", "fill-opacity", "fill-rule",
        "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit",
        "stroke-dasharray", "stroke-dashoffset", "stroke-opacity",
        "opacity", "color", "display", "visibility", "overflow",
        "clip-path", "clip-rule", "mask", "filter", "mix-blend-mode", "isolation",
        "vector-effect", "shape-rendering", "paint-order",
        "offset", "stop-color", "stop-opacity",
        "gradientUnits", "gradientTransform", "spreadMethod",
        "patternUnits", "patternContentUnits", "patternTransform",
        "clipPathUnits", "maskUnits", "maskContentUnits",
        "markerWidth", "markerHeight", "markerUnits", "refX", "refY", "orient",
        "marker-start", "marker-mid", "marker-end",
        "font-size", "font-family", "font-weight", "font-style", "font-variant",
        "text-anchor", "dominant-baseline", "alignment-baseline", "baseline-shift",
        "letter-spacing", "word-spacing", "text-decoration", "writing-mode",
        "dx", "dy", "rotate", "textLength", "lengthAdjust", "startOffset",
        "href");

    private static readonly ImmutableArray<string> RasterMediaTypes =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];

    private static readonly ImmutableArray<string> AllowedSchemes = ["http", "https", "content"];

    // The only headers an SVG data URI may carry — nothing else is a parameter an icon needs.
    private static readonly ImmutableArray<string> SvgDataUriHeaders =
    [
        "data:image/svg+xml,",
        "data:image/svg+xml;utf8,",
        "data:image/svg+xml;charset=utf-8,",
        "data:image/svg+xml;base64,",
    ];

    // The only functions an attribute value may call: colours, transforms, and url(#fragment).
    private static readonly ImmutableHashSet<string> AllowedFunctions = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "url", "rgb", "rgba", "hsl", "hsla",
        "matrix", "translate", "scale", "rotate", "skewX", "skewY");

    // Markup and quoting characters: none of these belongs in a glyph name, an emoji or a URL, and
    // each is what lets a value escape the element or attribute a renderer put it in.
    private const string MarkupCharacters = "<>\"'`\\";

    /// <summary>
    /// Judges an icon value. Returns <c>null</c> when it is acceptable, otherwise one English
    /// sentence naming what was refused (the element, attribute, scheme or character).
    /// </summary>
    /// <param name="icon">The candidate <see cref="MeshNode.Icon"/>.</param>
    /// <returns><c>null</c> for an acceptable icon; the refusal otherwise.</returns>
    public static string? Refusal(string? icon)
    {
        // The bound applies to EVERY non-null value — checked before anything scans the string,
        // blank values included.
        if (icon is null)
            return null;
        if (icon.Length > MaxIconLength)
            return $"an icon is at most {MaxIconLength} characters (this one has {icon.Length})";
        if (string.IsNullOrWhiteSpace(icon))
            return null;

        var value = icon.Trim();

        // Anything that opens with markup is judged as an SVG document — the ONE markup an icon may
        // be. "<img …>", "<?xml-stylesheet …?><svg …>" or "<script>" fail there, by name.
        if (value[0] == '<')
            return SvgRefusal(value);

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DataUriRefusal(value);

        if (Scheme(value) is { } scheme)
        {
            if (!AllowedSchemes.Contains(scheme.ToLowerInvariant()))
                return $"the URL scheme '{scheme}:' is not allowed — an icon URL is http(s), "
                       + "relative, or a content: reference";
            return CharacterRefusal(value, "URL");
        }

        if (value.Contains('/'))
            return CharacterRefusal(value, "URL");

        if (value.Length > MaxTextLength)
            return $"a text or emoji icon is at most {MaxTextLength} characters (this one has {value.Length})";
        if (value.Contains('&'))
            return "a text or emoji icon may not contain '&'";
        return CharacterRefusal(value, "text or emoji icon");
    }

    /// <summary>
    /// The refusal sentence every icon write boundary uses — the validator chain and the upsert's
    /// update branch alike, so a caller reads one wording whichever verb it used.
    /// </summary>
    /// <param name="path">The node being written.</param>
    /// <param name="refusal">The detail from <see cref="Refusal"/>.</param>
    /// <returns>The full refusal.</returns>
    public static string RefusalMessage(string? path, string refusal) =>
        $"The icon of '{path}' was refused: {refusal}. An icon is a Fluent icon name, an emoji or short "
        + "text, an http(s), relative or content: URL, a png/jpeg/gif/webp data: URI, or an SVG made "
        + "of drawing elements only — no script, event handlers, foreign content or references outside "
        + "the icon.";

    /// <summary>Whether <paramref name="icon"/> is acceptable — <see cref="Refusal"/> is null.</summary>
    /// <param name="icon">The candidate icon.</param>
    /// <returns><c>true</c> when the icon may be written.</returns>
    public static bool IsAcceptable(string? icon) => Refusal(icon) is null;

    /// <summary>
    /// The scheme of <paramref name="value"/> when it carries one: letters, digits, '+', '-', '.'
    /// before a ':' that precedes any '/', '?' or '#'. Null for a relative path or plain text.
    /// </summary>
    private static string? Scheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
            return colon == 0 ? ":" : null;
        var stop = value.IndexOfAny(['/', '?', '#']);
        if (stop >= 0 && stop < colon)
            return null;
        return value[..colon];
    }

    private static string? CharacterRefusal(string value, string what)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c) || (char.IsWhiteSpace(c) && what == "URL"))
                return $"a {what} may not contain whitespace or control characters";
            if (MarkupCharacters.Contains(c))
                return $"a {what} may not contain the character '{c}'";
        }
        return null;
    }

    private static string? DataUriRefusal(string value)
    {
        if (value.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
            return SvgDataUriRefusal(value);

        var comma = value.IndexOf(',');
        if (comma < 0)
            return "a data: icon has no payload";
        var mediaType = value["data:".Length..comma].Split(';')[0].Trim().ToLowerInvariant();

        // A raster URI is judged as a WHOLE against one exact grammar —
        // `data:image/(png|jpeg|gif|webp);base64,` followed by base64 characters only — so its
        // header can carry nothing (no extra parameters, quotes or whitespace) that a renderer
        // placing it inside an attribute would have to escape.
        foreach (var rasterType in RasterMediaTypes)
        {
            var prefix = $"data:{rasterType};base64,";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.Length == prefix.Length)
                return $"a {rasterType} data: icon has no payload";
            for (var i = prefix.Length; i < value.Length; i++)
            {
                var c = value[i];
                if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
                    return $"a {rasterType} data: icon must be exactly 'data:{rasterType};base64,' followed by base64 characters";
            }
            return null;
        }
        return RasterMediaTypes.Contains(mediaType)
            ? $"a {mediaType} data: icon must be exactly 'data:{mediaType};base64,' followed by base64 characters"
            : $"a data: icon may only carry {string.Join(", ", RasterMediaTypes)} or image/svg+xml, not '{mediaType}'";
    }

    /// <summary>
    /// An SVG data URI is judged as a WHOLE before anything is decoded: its header must be one of
    /// <see cref="SvgDataUriHeaders"/> exactly, and the payload may carry none of <c>&amp; # ;</c>
    /// (nor quotes, angle brackets, backslash, whitespace or control characters). A renderer that
    /// places the URI in HTML decodes character references and treats '#' as the start of a
    /// fragment — so a payload able to spell either could make the URI that loads differ from the
    /// document this rule parsed.
    /// </summary>
    private static string? SvgDataUriRefusal(string value)
    {
        string? header = null;
        foreach (var candidate in SvgDataUriHeaders)
            if (value.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                header = candidate;
                break;
            }
        if (header is null)
            return "a data:image/svg+xml icon must start with exactly one of "
                   + string.Join(", ", SvgDataUriHeaders.Select(h => $"'{h}'"));

        var payload = value[header.Length..];
        foreach (var c in payload)
            if (c is '&' or '#' or ';' or '"' or '<' or '>' or '`' or '\\'
                || char.IsControl(c) || char.IsWhiteSpace(c))
                return "a data:image/svg+xml icon's payload must be percent- or base64-encoded (it carries a raw "
                       + (char.IsWhiteSpace(c) || char.IsControl(c) ? "whitespace or control character" : $"'{c}'") + ")";

        string document;
        if (header.EndsWith(";base64,", StringComparison.Ordinal))
        {
            try
            {
                document = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch (FormatException)
            {
                return "a data:image/svg+xml icon's base64 payload does not decode";
            }
        }
        else
            document = Uri.UnescapeDataString(payload);

        var refusal = SvgRefusal(document.Trim());
        return refusal is null ? null : $"the SVG inside the data: URI is refused: {refusal}";
    }

    /// <summary>
    /// Judges an SVG document: null when it holds only allowed drawing content, otherwise the
    /// refusal. Exposed for renderers and editors that want the same verdict before they write.
    /// </summary>
    /// <param name="svg">The SVG markup.</param>
    /// <returns><c>null</c> for an acceptable SVG; the refusal otherwise.</returns>
    public static string? SvgRefusal(string svg)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = false,
            MaxCharactersInDocument = MaxSvgCharacters,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), settings);
            var sawRoot = false;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.ProcessingInstruction:
                        return $"an SVG icon may not carry a processing instruction ('<?{reader.Name}')";

                    // Neither is drawing content, and both change meaning when an HTML parser
                    // reads the markup instead of an XML one — so refused, never ignored.
                    case XmlNodeType.CDATA:
                        return "an SVG icon may not carry a CDATA section";
                    case XmlNodeType.Comment:
                        return "an SVG icon may not carry a comment";
                    case XmlNodeType.DocumentType:
                        return "an SVG icon may not carry a DOCTYPE";

                    case XmlNodeType.Text:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (reader.Value.IndexOfAny(['<', '>']) >= 0)
                            return "an SVG icon's text may not contain '<' or '>'";
                        break;

                    case XmlNodeType.Element:
                    {
                        var name = reader.LocalName;
                        if (!sawRoot)
                        {
                            if (name != "svg")
                                return $"an icon written as markup must be an <svg> document, not <{reader.Name}>";
                            sawRoot = true;
                        }
                        if (reader.NamespaceURI is not ("" or SvgNamespace))
                            return $"the element <{reader.Name}> is outside the SVG namespace";
                        if (!AllowedElements.Contains(name))
                            return $"the element <{name}> is not allowed in an SVG icon";
                        if (AttributeRefusal(reader) is { } attributeRefusal)
                            return attributeRefusal;
                        break;
                    }
                }
            }
            return sawRoot ? null : "an icon written as markup must be an <svg> document";
        }
        catch (XmlException ex)
        {
            return $"the SVG is not well-formed XML ({ex.Message})";
        }
    }

    private static string? AttributeRefusal(XmlReader reader)
    {
        if (!reader.MoveToFirstAttribute())
            return null;
        do
        {
            var local = reader.LocalName;
            var ns = reader.NamespaceURI;
            var value = reader.Value;

            if (ns == XmlnsNamespace)
                continue; // a namespace declaration; what it declares is judged per element/attribute
            if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                return $"the event-handler attribute '{reader.Name}' is not allowed in an SVG icon";

            var isHref = local == "href" && ns is ("" or XlinkNamespace);
            var allowed = ns switch
            {
                "" => AllowedAttributes.Contains(local)
                      || local.StartsWith("data-", StringComparison.Ordinal)
                      || local.StartsWith("aria-", StringComparison.Ordinal),
                XlinkNamespace => local == "href",
                XmlNamespace => local is "space" or "lang",
                _ => false,
            };
            if (!allowed)
                return $"the attribute '{reader.Name}' is not allowed in an SVG icon";

            // ASCII trimming only: a URL parser keeps U+00A0 and its kin, so a TrimStart that
            // skips them would read "&#160;#p" as a fragment while the browser loads a resource.
            if (isHref && !value.TrimStart(XmlSpace).StartsWith('#'))
                return $"the attribute '{reader.Name}' may only reference a fragment inside the icon ('#id')";

            if (ValueRefusal(reader.Name, value) is { } valueRefusal)
                return valueRefusal;
        }
        while (reader.MoveToNextAttribute());
        reader.MoveToElement();
        return null;
    }

    // XML's whitespace — the only whitespace an attribute value may carry.
    private static readonly char[] XmlSpace = [' ', '\t', '\r', '\n'];

    private static bool IsXmlSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static string? ValueRefusal(string attribute, string value)
    {
        if (value.Length > MaxAttributeLength)
            return $"the attribute '{attribute}' is longer than {MaxAttributeLength} characters";

        // CSS escapes ("u\72l(") would let a function name or a scheme hide from every check
        // below, and no drawing attribute needs a backslash — so one is refused outright rather
        // than decoded. (XML character references are already decoded by the parser.)
        if (value.Contains('\\'))
            return $"the attribute '{attribute}' contains a backslash, which an SVG icon does not need";

        // A STRICT value grammar for every attribute: letters, digits, whitespace and
        // `# . , % + - _ ( )` — which is what colours, lengths, numbers, path data, transforms and
        // url(#fragment) are written in. Quotes are allowed in font-family alone. No ':' (so no
        // scheme), no '/', no ';', no '@'. Every function call must be on the allow-list, and
        // url( must hold a bare '#fragment'. One linear pass, no copies.
        var quotesAllowed = attribute == "font-family";
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            // Printable ASCII and XML whitespace only: any other character is refused here,
            // which closes the whole Unicode-whitespace and lookalike class at once.
            if (c > '~')
                return $"the attribute '{attribute}' contains the non-ASCII character U+{(int)c:X4}, which an SVG icon's values do not use";
            if (char.IsAsciiLetterOrDigit(c) || IsXmlSpace(c)
                || c is '#' or '.' or ',' or '%' or '+' or '-' or '_' or ')'
                || (quotesAllowed && c is '\'' or '"'))
                continue;
            if (c != '(')
                return $"the attribute '{attribute}' contains '{c}', which an SVG icon's values do not use";

            var nameEnd = i;
            var nameStart = nameEnd;
            while (nameStart > 0 && (char.IsAsciiLetter(value[nameStart - 1]) || value[nameStart - 1] == '-'))
                nameStart--;
            var function = value[nameStart..nameEnd];
            if (!AllowedFunctions.Contains(function))
                return $"the attribute '{attribute}' calls '{(function.Length == 0 ? "(" : function + "(")}', which an SVG icon may not use";
            if (function.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                var j = i + 1;
                while (j < value.Length && IsXmlSpace(value[j]))
                    j++;
                if (j >= value.Length || value[j] != '#')
                    return $"the attribute '{attribute}' references something outside the icon (url(…) must be a '#fragment')";
            }
        }
        return null;
    }
}
