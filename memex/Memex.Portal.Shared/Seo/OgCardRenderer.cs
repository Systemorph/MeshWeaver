using System.Reflection;
using System.Text;
using SkiaSharp;

namespace Memex.Portal.Shared.Seo;

/// <summary>
/// Draws the 1200×630 share card a public page falls back to when it has authored no image of
/// its own — so "this page has an Open Graph card" is the DEFAULT, not something each page has to
/// remember to do.
///
/// <para><b>Why this exists.</b> Before it, <c>og:image</c> was emitted only when a node happened
/// to carry an authored image, and in practice no store plugin ever did (the resolver read
/// <c>poster</c>/<c>thumbnail</c> while <c>PluginContent</c> declares <c>ogImage</c> — the names
/// never met). Every share of every public page was a bare text link.</para>
///
/// <para><b>Why Skia, and why a font file.</b> The <c>NoDependencies</c> native build links no
/// fontconfig and no freetype, so the portal image needs nothing apt-installed and its base image
/// is untouched; the flip side is that Skia can then find no system font at all, so the card
/// carries its own (Open Sans, Apache-2.0). Latin/Greek/Cyrillic render; a CJK title falls back to
/// tofu, which is why <see cref="Draw"/> keeps the node name legible through layout (size, colour,
/// position) rather than through glyphs alone.</para>
///
/// <para>Registered as a singleton — the typeface is decoded once and held as an INSTANCE field,
/// never a static cache.</para>
/// </summary>
public sealed class OgCardRenderer : IDisposable
{
    private const int Width = 1200;
    private const int Height = 630;
    private const int Margin = 84;

    private readonly SKTypeface typeface;
    private readonly string siteName;

    /// <summary>Creates the renderer, decoding the embedded font once.</summary>
    /// <param name="siteName">The instance name printed as the card's eyebrow.</param>
    public OgCardRenderer(string siteName)
    {
        this.siteName = string.IsNullOrWhiteSpace(siteName) ? "Memex" : siteName.Trim();
        using var stream = typeof(OgCardRenderer).GetTypeInfo().Assembly
            .GetManifestResourceStream("Memex.Portal.Shared.Seo.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException(
                "The embedded share-card font is missing. Without it the NoDependencies Skia build "
                + "has no font at all and every card would render blank — check the EmbeddedResource "
                + "LogicalName in Memex.Portal.Shared.csproj.");
        typeface = SKTypeface.FromStream(stream)
            ?? throw new InvalidOperationException("The embedded share-card font could not be decoded.");
    }

    /// <summary>
    /// Renders the card as PNG bytes. Pure: same inputs → same bytes, which is what lets the
    /// endpoint serve a strong ETag and let crawlers cache hard.
    /// </summary>
    /// <param name="title">The headline — the node's name.</param>
    /// <param name="description">The supporting line; may be null or empty.</param>
    /// <param name="eyebrow">Small label above the title (category or node type); may be null.</param>
    /// <param name="accentSeed">Stable string (the node path) the accent colour is derived from.</param>
    public byte[] Render(string title, string? description, string? eyebrow, string accentSeed)
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        Draw(surface.Canvas, title, description, eyebrow, AccentFor(accentSeed));
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void Draw(SKCanvas canvas, string title, string? description, string? eyebrow, SKColor accent)
    {
        canvas.Clear(new SKColor(0x0B, 0x11, 0x20));

        // Ground: a dark vertical gradient, then a wide accent glow anchored bottom-left so the
        // card reads as lit rather than flat-filled.
        using (var bg = new SKPaint())
        {
            bg.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, Height),
                [new SKColor(0x0F, 0x17, 0x2A), new SKColor(0x08, 0x0C, 0x18)],
                null, SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(0, 0, Width, Height), bg);
        }
        using (var glow = new SKPaint())
        {
            glow.Shader = SKShader.CreateRadialGradient(
                new SKPoint(Margin, Height), Height * 1.1f,
                [accent.WithAlpha(0x4E), accent.WithAlpha(0x00)],
                null, SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(0, 0, Width, Height), glow);
        }

        // The accent rule: the one hard edge on the card, and the thing that makes a row of
        // shared links read as one family.
        using (var rule = new SKPaint { Color = accent, IsAntialias = true })
            canvas.DrawRect(new SKRect(0, 0, Width, 10), rule);

        using var ink = new SKPaint { IsAntialias = true };
        var textWidth = (float)(Width - (Margin * 2));

        // Lay the block out BEFORE drawing any of it, so the whole thing can be centred in the
        // space between the top rule and the footer. Top-anchoring looks composed only when a
        // description happens to be long; a bare title (no description authored — the common case
        // for a Space) left a dead third of the card empty and read as unfinished.
        var hasEyebrow = !string.IsNullOrWhiteSpace(eyebrow);
        const float EyebrowBlock = 66f;
        var titleLines = FitLines(title, 68, 44, 3, textWidth, out var titleSize);
        var titleLeading = titleSize * 1.14f;
        var descLines = string.IsNullOrWhiteSpace(description)
            ? []
            : Wrap(description!, new SKFont(typeface, 30), textWidth, 2);
        const float DescLeading = 42f;

        var blockHeight = (hasEyebrow ? EyebrowBlock : 0)
                          + (titleLines.Count * titleLeading)
                          + (descLines.Count > 0 ? 34 + (descLines.Count * DescLeading) : 0);

        var areaTop = (float)Margin;
        var areaBottom = Height - Margin - 30f;          // above the footer line
        var y = areaTop + Math.Max(0, ((areaBottom - areaTop) - blockHeight) / 2f);

        if (hasEyebrow)
        {
            using var eyebrowFont = new SKFont(typeface, 23);
            ink.Color = accent;
            y += 24;
            canvas.DrawText(Spaced(eyebrow!.ToUpperInvariant()), Margin, y, SKTextAlign.Left, eyebrowFont, ink);
            y += EyebrowBlock - 24;
        }

        using (var titleFont = new SKFont(typeface, titleSize) { Embolden = true })
        {
            ink.Color = new SKColor(0xF8, 0xFA, 0xFC);
            foreach (var line in titleLines)
            {
                y += titleLeading;
                canvas.DrawText(line, Margin, y, SKTextAlign.Left, titleFont, ink);
            }
        }

        if (descLines.Count > 0)
        {
            using var descFont = new SKFont(typeface, 30);
            ink.Color = new SKColor(0x94, 0xA3, 0xB8);
            y += 34;
            foreach (var line in descLines)
            {
                y += DescLeading;
                canvas.DrawText(line, Margin, y, SKTextAlign.Left, descFont, ink);
            }
        }

        // Footer: the instance, so a card lifted into a feed still says where it came from.
        using (var footFont = new SKFont(typeface, 24))
        {
            ink.Color = new SKColor(0x64, 0x74, 0x8B);
            canvas.DrawText(siteName, Margin, Height - Margin + 10, SKTextAlign.Left, footFont, ink);
        }
    }

    /// <summary>
    /// The largest size in [<paramref name="min"/>, <paramref name="max"/>] at which
    /// <paramref name="text"/> wraps into at most <paramref name="maxLines"/> lines.
    /// </summary>
    private List<string> FitLines(
        string text, float max, float min, int maxLines, float width, out float size)
    {
        for (size = max; size > min; size -= 3)
        {
            using var probe = new SKFont(typeface, size) { Embolden = true };
            var lines = Wrap(text, probe, width, maxLines + 1);
            if (lines.Count <= maxLines)
                return lines;
        }
        size = min;
        using var floorFont = new SKFont(typeface, size) { Embolden = true };
        return Wrap(text, floorFont, width, maxLines);
    }

    /// <summary>
    /// Greedy word wrap to <paramref name="width"/>, capped at <paramref name="maxLines"/>; the
    /// last line is ellipsized when text remains. A single word longer than the line is broken
    /// mid-word rather than overflowing the card.
    /// </summary>
    private static List<string> Wrap(string text, SKFont font, float width, int maxLines)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (font.MeasureText(candidate) <= width)
            {
                line.Clear().Append(candidate);
                continue;
            }
            if (line.Length > 0)
            {
                lines.Add(line.ToString());
                line.Clear();
                if (lines.Count == maxLines)
                    return Ellipsize(lines, font, width);
            }
            // A word that cannot fit alone is chopped to what does.
            if (font.MeasureText(word) > width)
            {
                var cut = word;
                while (cut.Length > 1 && font.MeasureText(cut) > width)
                    cut = cut[..^1];
                lines.Add(cut);
                if (lines.Count == maxLines)
                    return Ellipsize(lines, font, width);
                continue;
            }
            line.Append(word);
        }

        if (line.Length > 0 && lines.Count < maxLines)
            lines.Add(line.ToString());
        else if (line.Length > 0)
            return Ellipsize(lines, font, width);
        return lines;
    }

    private static List<string> Ellipsize(List<string> lines, SKFont font, float width)
    {
        if (lines.Count == 0)
            return lines;
        var last = lines[^1];
        while (last.Length > 1 && font.MeasureText(last + "…") > width)
            last = last[..^1];
        lines[^1] = last.TrimEnd() + "…";
        return lines;
    }

    /// <summary>Letter-spacing for the small-caps eyebrow — Skia has no tracking property.</summary>
    private static string Spaced(string text) => string.Join(" ", text.ToCharArray());

    /// <summary>
    /// A stable accent per node: the same page always shares in the same colour, and different
    /// pages differ. Hues are drawn from a fixed set so every card stays on-brand instead of
    /// landing on whatever an unconstrained hash produces (mud, or neon).
    /// </summary>
    internal static SKColor AccentFor(string seed)
    {
        SKColor[] palette =
        [
            new(0x38, 0xBD, 0xF8),   // sky
            new(0x2D, 0xD4, 0xBF),   // teal
            new(0xA7, 0x8B, 0xFA),   // violet
            new(0xF5, 0x9E, 0x0B),   // amber
            new(0xF4, 0x72, 0xB6),   // pink
            new(0x4A, 0xDE, 0x80),   // green
        ];
        var hash = 17;
        foreach (var c in seed ?? "")
            hash = unchecked((hash * 31) + c);
        return palette[Math.Abs(hash) % palette.Length];
    }

    /// <inheritdoc />
    public void Dispose() => typeface.Dispose();
}
