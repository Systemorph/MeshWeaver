using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.AI;

/// <summary>
/// Pure, deterministic identicon generator: turns a stable seed (a thread's id and/or
/// title) into a small, self-contained inline SVG avatar so every thread renders a
/// distinct visual in the catalog. The SAME seed always yields the SAME SVG; different
/// seeds are visually distinct.
///
/// <para>No external dependencies, no AI, no process-randomness: a SHA-256 of the seed
/// drives a 5×5 vertically-mirrored cell grid over a rounded background, coloured from a
/// hue derived off the same hash (<see cref="string.GetHashCode()"/> is deliberately
/// avoided — it is randomised per process and would break determinism across runs).</para>
///
/// <para>The output is sanitiser-safe by construction: only <c>&lt;svg&gt;</c> and
/// <c>&lt;rect&gt;</c> elements with static geometry + <c>fill</c> attributes — no
/// <c>script</c>, <c>foreignObject</c>, or <c>on*</c> handlers — so it passes straight
/// through the node card's inline-SVG rendering path (an <see cref="System.String"/> that
/// starts with <c>&lt;svg</c>).</para>
/// </summary>
public static class ThreadIconGenerator
{
    // 5×5 identicon grid, 16px per cell → a clean 80×80 viewBox (no explicit width/height,
    // so the icon scales to whatever the card sizes it to, like the line-art agent icons).
    private const int Grid = 5;
    private const int Cell = 16;
    private const int Size = Grid * Cell;

    /// <summary>
    /// Produces a deterministic inline SVG identicon for <paramref name="seed"/> (typically a
    /// thread's speaking id, optionally combined with its title). Returns a compact single-line
    /// <c>&lt;svg …&gt;…&lt;/svg&gt;</c> string. Never throws; a null/empty seed still yields a
    /// valid (constant) icon.
    /// </summary>
    /// <param name="seed">The stable seed to hash — same seed ⇒ same icon.</param>
    /// <returns>Inline SVG markup, always starting with <c>&lt;svg</c> and ending with <c>&lt;/svg&gt;</c>.</returns>
    public static string Generate(string? seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed ?? string.Empty));

        // Hue from the first two bytes → 0..359; fixed saturation/lightness for a legible palette.
        var hue = ((hash[0] << 8) | hash[1]) % 360;
        var fg = HslToHex(hue, 0.60, 0.50);          // saturated foreground cells
        var bg = HslToHex(hue, 0.45, 0.93);          // soft same-hue background

        var sb = new StringBuilder(512);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
          .Append(Size).Append(' ').Append(Size).Append("\">");

        // Rounded background tile.
        sb.Append("<rect width=\"").Append(Size).Append("\" height=\"").Append(Size)
          .Append("\" rx=\"").Append(Cell).Append("\" fill=\"").Append(bg).Append("\"/>");

        // Left half (columns 0..2) chosen by hash bits, mirrored onto the right half so the
        // avatar is vertically symmetric (the classic GitHub-identicon shape). 3 cols × 5 rows
        // = 15 bits, one taken from each of hash bytes 16..30 (well past the 2 hue bytes; SHA-256 has 32).
        var bit = 16;
        for (var col = 0; col < (Grid + 1) / 2; col++)
        {
            for (var row = 0; row < Grid; row++, bit++)
            {
                var on = ((hash[bit % hash.Length] >> (bit % 8)) & 1) == 1;
                if (!on) continue;
                AppendCell(sb, col, row, fg);
                var mirror = Grid - 1 - col;
                if (mirror != col)
                    AppendCell(sb, mirror, row, fg);
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void AppendCell(StringBuilder sb, int col, int row, string fill) =>
        sb.Append("<rect x=\"").Append(col * Cell).Append("\" y=\"").Append(row * Cell)
          .Append("\" width=\"").Append(Cell).Append("\" height=\"").Append(Cell)
          .Append("\" fill=\"").Append(fill).Append("\"/>");

    // Deterministic HSL → "#rrggbb". h in [0,360); s,l in [0,1].
    private static string HslToHex(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var hp = h / 60.0;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;
        switch ((int)hp)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        var m = l - c / 2;
        return string.Create(CultureInfo.InvariantCulture,
            $"#{To255(r + m):x2}{To255(g + m):x2}{To255(b + m):x2}");
    }

    private static int To255(double v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
