using System.Globalization;
using System.Text;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Draws a stand-in node-type icon for a name that has no SVG shipped for it, so a missing icon
/// renders as a deliberate-looking glyph instead of a broken image.
///
/// <para><b>Why this is generated rather than fetched.</b> It runs on the request path. Asking a
/// model for a picture there would trade a broken image for latency, cost, and a new failure mode —
/// and the failure mode would be worse, because it fails at render time rather than at build time.
/// This is pure string building: no I/O, no hub, no allocation worth measuring, and it cannot fail.
/// Model-authored icons are the right way to *fill the gap permanently* — generate one, commit it to
/// <c>MeshWeaver.Graph/Icons</c>, and this stand-in stops being reached for that name.</para>
///
/// <para><b>Deterministic on purpose.</b> The same name always yields the same glyph, so it is
/// cacheable under the route's 30-day public cache, stable across restarts and replicas, and does
/// not flicker between renders. Hue comes from an FNV-1a hash of the name; the letters are the
/// name's own initials.</para>
/// </summary>
internal static class GeneratedIcon
{
    /// <summary>
    /// Builds an SVG stand-in for <paramref name="fileName"/> (e.g. <c>server.svg</c>, or a bare
    /// name). Always returns markup — there is no failure path.
    /// </summary>
    internal static byte[] For(string fileName)
    {
        var name = StripExtension(fileName);
        var initials = InitialsOf(name);
        var hue = HueOf(name);

        // Two tones of one hue: a soft plate and a stronger glyph. Saturation and lightness are
        // fixed so every generated icon carries the same weight — only the hue varies — and a wall
        // of them reads as a set rather than as noise.
        var plate = $"hsl({hue} 62% 92%)";
        var ink = $"hsl({hue} 58% 34%)";
        var edge = $"hsl({hue} 45% 78%)";

        // 48x48 with a 10px radius matches the shipped icons, so a generated one drops into the
        // same grid without resizing. Font-size steps down for two letters so both fit the plate.
        var fontSize = initials.Length > 1 ? 17 : 22;

        var svg = new StringBuilder(512)
            .Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 48 48\" role=\"img\" aria-label=\"")
            .Append(XmlEscape(name))
            .Append("\"><title>").Append(XmlEscape(name)).Append("</title>")
            .Append("<rect width=\"48\" height=\"48\" rx=\"10\" fill=\"").Append(plate).Append("\"/>")
            .Append("<rect x=\".75\" y=\".75\" width=\"46.5\" height=\"46.5\" rx=\"9.25\" fill=\"none\" stroke=\"")
            .Append(edge).Append("\" stroke-width=\"1.5\"/>")
            .Append("<text x=\"24\" y=\"24\" text-anchor=\"middle\" dominant-baseline=\"central\" ")
            .Append("font-family=\"system-ui,-apple-system,Segoe UI,Roboto,sans-serif\" font-weight=\"600\" font-size=\"")
            .Append(fontSize.ToString(CultureInfo.InvariantCulture))
            .Append("\" fill=\"").Append(ink).Append("\">")
            .Append(XmlEscape(initials))
            .Append("</text></svg>")
            .ToString();

        return Encoding.UTF8.GetBytes(svg);
    }

    private static string StripExtension(string fileName)
    {
        var slash = fileName.LastIndexOf('/');
        var name = slash >= 0 ? fileName[(slash + 1)..] : fileName;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// One or two letters from the name. Segments split on <c>-</c> and <c>_</c>, so
    /// <c>task-list</c> reads as <c>TL</c> while <c>server</c> reads as <c>S</c>. A name with no
    /// letters at all falls back to <c>?</c> rather than rendering an empty plate.
    /// </summary>
    internal static string InitialsOf(string name)
    {
        var parts = name.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        var letters = new StringBuilder(2);
        foreach (var part in parts)
        {
            foreach (var ch in part)
            {
                if (!char.IsLetterOrDigit(ch)) continue;
                letters.Append(char.ToUpperInvariant(ch));
                break;
            }
            if (letters.Length == 2) break;
        }
        return letters.Length > 0 ? letters.ToString() : "?";
    }

    /// <summary>
    /// A stable hue in [0,360) from the name. FNV-1a over the lower-cased name: cheap, well spread
    /// for short strings, and — unlike <c>string.GetHashCode()</c> — identical across processes and
    /// runtime versions, which is what makes the result safe to cache publicly for 30 days.
    /// </summary>
    internal static int HueOf(string name)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var ch in name)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= prime;
        }
        return (int)(hash % 360);
    }

    private static string XmlEscape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
