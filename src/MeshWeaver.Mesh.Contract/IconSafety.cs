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
        "svg", "g", "defs", "symbol", "use", "title", "desc",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textPath",
        "linearGradient", "radialGradient", "stop", "pattern",
        "clipPath", "mask", "marker");

    private static readonly ImmutableHashSet<string> AllowedAttributes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "id", "class", "style", "version", "role", "focusable", "lang",
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

    // Matched against an attribute value with case folded and whitespace / control characters
    // removed, the way a URL or CSS parser reads it.
    private static readonly ImmutableArray<string> ForbiddenValueFragments =
        ["javascript:", "vbscript:", "data:", "expression(", "-moz-binding", "behavior:", "@import"];

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
        if (string.IsNullOrWhiteSpace(icon))
            return null;

        if (icon.Length > MaxIconLength)
            return $"an icon is at most {MaxIconLength} characters (this one has {icon.Length})";

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
        var comma = value.IndexOf(',');
        if (comma < 0)
            return "a data: icon has no payload";
        var header = value["data:".Length..comma];
        var payload = value[(comma + 1)..];
        var parts = header.Split(';');
        var mediaType = parts[0].Trim().ToLowerInvariant();
        var isBase64 = parts.Skip(1).Any(p => p.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase));

        if (mediaType == "image/svg+xml")
        {
            // The URI itself sits in an attribute; a percent-encoded document never needs these raw.
            foreach (var c in value)
                if (c is '"' or '<' or '>' or '`' or '\\' || char.IsControl(c) || char.IsWhiteSpace(c))
                    return "a data:image/svg+xml icon must be percent- or base64-encoded (it carries a raw "
                           + (char.IsWhiteSpace(c) || char.IsControl(c) ? "whitespace or control character" : $"'{c}'") + ")";
            string document;
            try
            {
                document = isBase64
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                    : Uri.UnescapeDataString(payload);
            }
            catch (FormatException)
            {
                return "a data:image/svg+xml icon's base64 payload does not decode";
            }
            var refusal = SvgRefusal(document.Trim());
            return refusal is null ? null : $"the SVG inside the data: URI is refused: {refusal}";
        }

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
            IgnoreComments = true,
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

            if (isHref && !value.TrimStart().StartsWith('#'))
                return $"the attribute '{reader.Name}' may only reference a fragment inside the icon ('#id')";

            if (ValueRefusal(reader.Name, value) is { } valueRefusal)
                return valueRefusal;
        }
        while (reader.MoveToNextAttribute());
        reader.MoveToElement();
        return null;
    }

    private static string? ValueRefusal(string attribute, string value)
    {
        if (value.Length > MaxAttributeLength)
            return $"the attribute '{attribute}' is longer than {MaxAttributeLength} characters";

        // CSS escapes ("u\72l(") would let a function name or a scheme hide from every check
        // below, and no drawing attribute needs a backslash — so one is refused outright rather
        // than decoded. (XML character references are already decoded by the parser.)
        if (value.Contains('\\'))
            return $"the attribute '{attribute}' contains a backslash, which an SVG icon does not need";

        // Normalise the way a URL or CSS parser would read it: case, and the whitespace / control
        // characters that browsers skip inside a scheme ("java\tscript:").
        var normalised = new StringBuilder(value.Length);
        foreach (var c in value)
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
                normalised.Append(char.ToLowerInvariant(c));
        var flat = normalised.ToString();

        foreach (var forbidden in ForbiddenValueFragments)
            if (flat.Contains(forbidden, StringComparison.Ordinal))
                return $"the attribute '{attribute}' carries '{forbidden}', which is not allowed in an SVG icon";

        // Every url(…) must point INSIDE the document. Scanned by index — no per-occurrence copy,
        // so the cost is linear in the attribute length however many url( it repeats.
        var at = 0;
        while ((at = flat.IndexOf("url(", at, StringComparison.Ordinal)) >= 0)
        {
            var target = at + 4;
            while (target < flat.Length && flat[target] is '"' or '\'')
                target++;
            if (target >= flat.Length || flat[target] != '#')
                return $"the attribute '{attribute}' references something outside the icon (url(…) must be a '#fragment')";
            at = target;
        }
        return null;
    }
}
