using System.Text.RegularExpressions;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Reads the icon an inert <c>&lt;img&gt;</c> carries. Every render path emits an inline-svg icon as
/// <c>&lt;img src="data:image/svg+xml,…"&gt;</c> (<see cref="MeshNodeImageHelper.InlineSvgImage"/>), so an
/// assertion about WHAT the icon draws — its plate, its glyph — has to read the decoded document,
/// while an assertion about what the PAGE receives reads the markup as it stands.
/// </summary>
internal static partial class RenderedSvg
{
    /// <summary><paramref name="markup"/> with every svg <c>data:</c> URI in it replaced by the svg
    /// document it encodes.</summary>
    public static string Decoded(string markup) =>
        SvgDataUri().Replace(markup, m => Uri.UnescapeDataString(m.Groups["svg"].Value));

    [GeneratedRegex(@"data:image/svg\+xml,(?<svg>[^""'\s)]+)")]
    private static partial Regex SvgDataUri();
}
