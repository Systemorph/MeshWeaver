using System.Reflection;
using System.Text;
using SkiaSharp;

namespace Memex.Portal.Shared.Seo;

/// <summary>
/// Everything the share card can say about one page — read off the <c>MeshNode</c> by the
/// endpoint, never authored a second time. Init-only properties, no primary constructor: a
/// record's primary constructor is a binary contract with every module compiled against it (see
/// <see cref="PageIcon.Rel"/>), and this shape will grow as nodes learn to say more about themselves.
/// </summary>
public sealed record OgCardContent
{
    /// <summary>The headline — the node's name.</summary>
    public required string Title { get; init; }

    /// <summary>The supporting text — the node's description, abstract or tagline.</summary>
    public string? Description { get; init; }

    /// <summary>The small label above the title — the node's category, else its type.</summary>
    public string? Eyebrow { get; init; }

    /// <summary>The node's own mark as inline <c>&lt;svg&gt;</c> markup (backplated), drawn large
    /// on the card. Null draws the default badge instead, so every card carries a picture.</summary>
    public string? IconSvg { get; init; }

    /// <summary>A price label such as <c>CHF 490</c>, shown as a chip; null for pages that sell nothing.</summary>
    public string? Price { get; init; }

    /// <summary>The node path, printed in the footer so a card lifted into a feed still says WHERE
    /// on the instance it points.</summary>
    public string? Path { get; init; }

    /// <summary>Stable string (the node path) the accent colour is derived from.</summary>
    public string AccentSeed { get; init; } = "";
}

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
/// <para><b>What it draws.</b> Whatever the node can say about itself: category as the eyebrow,
/// name as the title, description, a price chip for something that is for sale, the node's OWN
/// mark rendered large on the right, and the instance name plus the path in the footer. A node
/// with no mark gets a default badge — a rounded tile in the card's accent carrying the page's
/// initial — so no card is ever text on a dark rectangle (2026-09-18: the Store shared into
/// iMessage as a bare title, and the tiny site favicon was the only picture on the bubble).</para>
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
    /// <summary>The card's pixel width — the Open Graph recommended size, declared in the head as <c>og:image:width</c>.</summary>
    public const int Width = 1200;

    /// <summary>The card's pixel height — declared in the head as <c>og:image:height</c>.</summary>
    public const int Height = 630;

    private const int Margin = 84;

    /// <summary>The edge of the square the icon (or default badge) is drawn in.</summary>
    internal const int IconSize = 264;

    /// <summary>Gap between the text column and the icon square.</summary>
    private const int IconGap = 64;

    /// <summary>Left edge of the icon square — the text column ends <see cref="IconGap"/> before it.</summary>
    internal const int IconLeft = Width - Margin - IconSize;

    /// <summary>Vertical centre of the content band (below the rule, above the footer).</summary>
    internal const float ContentCentreY = (Margin + (Height - Margin - 30f)) / 2f;

    private static readonly SKColor Paper = new(0xF8, 0xFA, 0xFC);
    private static readonly SKColor Muted = new(0x94, 0xA3, 0xB8);
    private static readonly SKColor Faint = new(0x64, 0x74, 0x8B);

    private readonly SKTypeface typeface;
    private readonly string siteName;

    /// <summary>Creates the renderer, decoding the embedded font once.</summary>
    /// <param name="siteName">The instance name printed in the card's footer.</param>
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
    public byte[] Render(OgCardContent card)
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        Draw(surface.Canvas, card, AccentFor(card.AccentSeed));
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Renders the card from its parts. Kept for callers compiled against the four-argument shape;
    /// new callers pass an <see cref="OgCardContent"/>, which is the only way to reach the icon,
    /// the price chip and the path.
    /// </summary>
    /// <param name="title">The headline — the node's name.</param>
    /// <param name="description">The supporting line; may be null or empty.</param>
    /// <param name="eyebrow">Small label above the title (category or node type); may be null.</param>
    /// <param name="accentSeed">Stable string (the node path) the accent colour is derived from.</param>
    public byte[] Render(string title, string? description, string? eyebrow, string accentSeed) =>
        Render(new OgCardContent
        {
            Title = title,
            Description = description,
            Eyebrow = eyebrow,
            AccentSeed = accentSeed,
        });

    /// <summary>
    /// The card for a page that is no public node — the home page, a route the resolver does not
    /// know, a node the anonymous gate withholds. It says only what is already public: the
    /// instance's name and host, under the MeshWeaver mark. Nothing about the node the request
    /// named reaches it.
    /// </summary>
    /// <param name="host">The host the page was served from, printed as the description.</param>
    public byte[] RenderSite(string? host) =>
        Render(new OgCardContent
        {
            Title = siteName,
            Description = string.IsNullOrWhiteSpace(host) ? null : host.Trim(),
            AccentSeed = siteName,
            IconSvg = MeshWeaverMarkTile,
        });

    /// <summary>
    /// The picture the instance card carries: the MeshWeaver mark — three nodes joined through a
    /// centre — in cyan on its navy tile, self-plated so it rasterises the same on every card.
    /// Before 2026-09-22 the instance card wore the default badge, the site name's initial on an
    /// accent tile, which is what an unbranded page gets; a share of the home page should show
    /// the brand, not an "M". Same drawing as the portal's <c>MeshWeaverLogo</c> and
    /// <c>wwwroot/favicon.svg</c>; the single source is Systemorph/Memex
    /// <c>Memex.Website/site/icon.svg</c>.
    /// </summary>
    internal const string MeshWeaverMarkTile =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'>" +
        "<rect width='64' height='64' rx='14' fill='#0a0e1a'/>" +
        "<g transform='translate(9 9) scale(0.72)' fill='none' stroke='#00d4ff' stroke-width='6' stroke-linecap='round' stroke-linejoin='round'>" +
        "<path d='M12 48 L32 14 L52 48 Z'/><path d='M32 38 L12 48 M32 38 L52 48 M32 38 L32 14'/></g>" +
        "<g transform='translate(9 9) scale(0.72)' fill='#00d4ff'><circle cx='12' cy='48' r='7'/><circle cx='52' cy='48' r='7'/>" +
        "<circle cx='32' cy='14' r='7'/><circle cx='32' cy='38' r='6'/></g></svg>";

    private void Draw(SKCanvas canvas, OgCardContent card, SKColor accent)
    {
        canvas.Clear(new SKColor(0x0B, 0x11, 0x20));

        // Ground: a dark vertical gradient, then two accent glows — a wide one bottom-left so the
        // card reads as lit rather than flat-filled, and a fainter one behind the icon so the mark
        // sits on a halo instead of floating on black.
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
        using (var halo = new SKPaint())
        {
            halo.Shader = SKShader.CreateRadialGradient(
                new SKPoint(IconLeft + (IconSize / 2f), ContentCentreY), IconSize * 1.25f,
                [accent.WithAlpha(0x30), accent.WithAlpha(0x00)],
                null, SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(0, 0, Width, Height), halo);
        }

        // The accent rule: the one hard edge on the card, and the thing that makes a row of
        // shared links read as one family.
        using (var rule = new SKPaint { Color = accent, IsAntialias = true })
            canvas.DrawRect(new SKRect(0, 0, Width, 10), rule);

        DrawIcon(canvas, card, accent);
        DrawText(canvas, card, accent);
        DrawFooter(canvas, card);
    }

    // ── The picture ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The node's own mark, or the default badge when it has none this can draw. Either way a
    /// picture — the whole point of the card over a text link.
    /// </summary>
    private void DrawIcon(SKCanvas canvas, OgCardContent card, SKColor accent)
    {
        var box = new SKRect(IconLeft, ContentCentreY - (IconSize / 2f), IconLeft + IconSize, ContentCentreY + (IconSize / 2f));

        // A soft shadow under whatever is drawn, so the mark reads as a tile lying on the card.
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 0x66) })
        {
            shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18);
            canvas.DrawRoundRect(new SKRect(box.Left + 6, box.Top + 14, box.Right + 6, box.Bottom + 14), 56, 56, shadow);
        }

        if (TryDrawSvg(canvas, card.IconSvg, box))
            return;
        DrawDefaultBadge(canvas, card, accent, box);
    }

    /// <summary>
    /// Draws inline svg scaled to fit <paramref name="box"/>, centred, through the SAME rasterizer
    /// the favicon route uses — so a mark that parses but paints nothing (an empty root) is
    /// refused here too, instead of leaving the icon square blank behind a picture that "exists".
    /// False when there is no svg or it cannot be drawn — an authored icon that fails here is a
    /// content defect, but the card is the wrong place to surface it; the caller draws the badge.
    /// </summary>
    private static bool TryDrawSvg(SKCanvas canvas, string? svg, SKRect box)
    {
        if (string.IsNullOrWhiteSpace(svg))
            return false;
        try
        {
            // Rendered at twice the box and downsampled, so curved marks keep their edges.
            using var image = IconRasterizer.RenderImage(svg, IconSize * 2);
            if (image is null)
                return false;
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(image, box, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The default picture: a rounded tile in the card's accent carrying the page's initial — the
    /// same idea as the per-instance favicon badge, at share-card scale. Drawn from the title so
    /// two icon-less pages still share with two different pictures.
    /// </summary>
    private void DrawDefaultBadge(SKCanvas canvas, OgCardContent card, SKColor accent, SKRect box)
    {
        using (var tile = new SKPaint { IsAntialias = true })
        {
            tile.Shader = SKShader.CreateLinearGradient(
                new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Bottom),
                [accent, Darken(accent, 0.62f)],
                null, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(box, 56, 56, tile);
        }
        // A thin lighter rim so the tile has an edge against the halo behind it.
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Paper.WithAlpha(0x40) })
            canvas.DrawRoundRect(box, 56, 56, rim);

        if ((Initial(card.Title) ?? Initial(siteName)) is not { } letter)
            return;
        using var font = new SKFont(typeface, 150) { Embolden = true };
        using var ink = new SKPaint { IsAntialias = true, Color = Paper };
        var metrics = font.Metrics;
        var baseline = box.MidY - ((metrics.Ascent + metrics.Descent) / 2f);
        canvas.DrawText(letter, box.MidX, baseline, SKTextAlign.Center, font, ink);
    }

    /// <summary>The first letter or digit of <paramref name="text"/>, upper-cased; null when it
    /// opens with nothing the embedded font can be trusted to draw (an emoji, punctuation).</summary>
    private static string? Initial(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        foreach (var rune in text.Trim().EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) && rune.IsBmp)
                return Rune.ToUpperInvariant(rune).ToString();
            return null;
        }
        return null;
    }

    private static SKColor Darken(SKColor colour, float factor) =>
        new((byte)(colour.Red * factor), (byte)(colour.Green * factor), (byte)(colour.Blue * factor), colour.Alpha);

    // ── The words ──────────────────────────────────────────────────────────────────────────

    private void DrawText(SKCanvas canvas, OgCardContent card, SKColor accent)
    {
        using var ink = new SKPaint { IsAntialias = true };
        var textWidth = (float)(IconLeft - IconGap - Margin);

        // Lay the block out BEFORE drawing any of it, so the whole thing can be centred beside the
        // icon. Top-anchoring looks composed only when a description happens to be long; a bare
        // title (no description authored — the common case for a Space) left a dead third of the
        // card empty and read as unfinished.
        var eyebrow = OneLine(card.Eyebrow);
        var hasEyebrow = eyebrow.Length > 0;
        const float EyebrowBlock = 66f;
        const float DescSize = 28f;
        const float DescLeading = 40f;
        const float DescGap = 30f;
        using var descFont = new SKFont(typeface, DescSize);
        var description = OneLine(card.Description);
        var price = OneLine(card.Price);
        const float ChipHeight = 50f;
        const float ChipGap = 30f;

        var areaTop = (float)Margin;
        var areaBottom = Height - Margin - 30f;          // above the footer line
        var fixedHeight = (hasEyebrow ? EyebrowBlock : 0) + (price.Length > 0 ? ChipGap + ChipHeight : 0);

        // The band is a budget, not a suggestion: a long title, a long description and a price
        // chip together overflowed into the footer (the first course card drawn). The title gets
        // up to three lines; if that leaves no room for at least two lines of description, the
        // title is refitted to two lines (ellipsized) — a card that names the page and says what
        // it is beats one that finishes the name and says nothing.
        var titleText = OneLine(card.Title);
        var titleLines = FitLines(titleText, 68, 44, 3, textWidth, out var titleSize);
        var titleLeading = titleSize * 1.14f;
        var descRoom = areaBottom - areaTop - fixedHeight - (titleLines.Count * titleLeading) - DescGap;
        if (description.Length > 0 && descRoom < 2 * DescLeading && titleLines.Count > 2)
        {
            titleLines = FitLines(titleText, 68, 44, 2, textWidth, out titleSize);
            titleLeading = titleSize * 1.14f;
            descRoom = areaBottom - areaTop - fixedHeight - (titleLines.Count * titleLeading) - DescGap;
        }
        var descMaxLines = Math.Clamp((int)Math.Floor(descRoom / DescLeading), 0, 3);
        var descLines = description.Length == 0 || descMaxLines == 0
            ? []
            : Wrap(description, descFont, textWidth, descMaxLines);

        var blockHeight = fixedHeight
                          + (titleLines.Count * titleLeading)
                          + (descLines.Count > 0 ? DescGap + (descLines.Count * DescLeading) : 0);

        var y = areaTop + Math.Max(0, ((areaBottom - areaTop) - blockHeight) / 2f);

        if (hasEyebrow)
        {
            using var eyebrowFont = new SKFont(typeface, 23);
            ink.Color = accent;
            y += 24;
            canvas.DrawText(Spaced(eyebrow.ToUpperInvariant()), Margin, y, SKTextAlign.Left, eyebrowFont, ink);
            y += EyebrowBlock - 24;
        }

        using (var titleFont = new SKFont(typeface, titleSize) { Embolden = true })
        {
            ink.Color = Paper;
            foreach (var line in titleLines)
            {
                y += titleLeading;
                canvas.DrawText(line, Margin, y, SKTextAlign.Left, titleFont, ink);
            }
        }

        if (descLines.Count > 0)
        {
            ink.Color = Muted;
            y += DescGap;
            foreach (var line in descLines)
            {
                y += DescLeading;
                canvas.DrawText(line, Margin, y, SKTextAlign.Left, descFont, ink);
            }
        }

        if (price.Length > 0)
        {
            y += ChipGap;
            DrawChip(canvas, price, Margin, y, ChipHeight, accent);
        }
    }

    /// <summary>A pill in the accent — the price, the one number worth putting on the picture.</summary>
    private void DrawChip(SKCanvas canvas, string text, float left, float top, float height, SKColor accent)
    {
        using var font = new SKFont(typeface, 25) { Embolden = true };
        const float PadX = 22f;
        var width = font.MeasureText(text) + (PadX * 2);
        var rect = new SKRect(left, top, left + width, top + height);

        using (var fill = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(0x26) })
            canvas.DrawRoundRect(rect, height / 2, height / 2, fill);
        using (var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = accent })
            canvas.DrawRoundRect(rect, height / 2, height / 2, stroke);

        using var ink = new SKPaint { IsAntialias = true, Color = accent };
        var metrics = font.Metrics;
        var baseline = rect.MidY - ((metrics.Ascent + metrics.Descent) / 2f);
        canvas.DrawText(text, left + PadX, baseline, SKTextAlign.Left, font, ink);
    }

    /// <summary>Footer: the instance on the left, the path on the right — so a card lifted into a
    /// feed still says where it came from and where on it.</summary>
    private void DrawFooter(SKCanvas canvas, OgCardContent card)
    {
        var baseline = Height - Margin + 10f;
        using var ink = new SKPaint { IsAntialias = true, Color = Faint };
        using var footFont = new SKFont(typeface, 24);
        canvas.DrawText(siteName, Margin, baseline, SKTextAlign.Left, footFont, ink);

        var path = OneLine(card.Path);
        if (path.Length == 0)
            return;
        var crumbs = path.Replace("/", "  ›  ");
        var room = Width - (Margin * 2) - footFont.MeasureText(siteName) - 60;
        using var pathFont = new SKFont(typeface, 22);
        var shown = Wrap(crumbs, pathFont, room, 1);
        if (shown.Count > 0)
            canvas.DrawText(shown[0], Width - Margin, baseline, SKTextAlign.Right, pathFont, ink);
    }

    // ── Layout helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Collapses any whitespace run (a markdown paragraph break, a tab) to one space.</summary>
    private static string OneLine(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
