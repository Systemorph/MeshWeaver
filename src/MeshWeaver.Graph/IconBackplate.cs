using System.Globalization;
using System.Text.RegularExpressions;

namespace MeshWeaver.Graph;

/// <summary>
/// The icon-backplate policy, made structural: every inline-svg icon the portal renders sits on a
/// full-bleed rounded backplate, and an icon authored WITHOUT one gets a generated plate at the
/// render seam instead of silently disappearing on the theme it was not authored for.
///
/// <para>🚨 Why this exists: a monochrome <c>currentColor</c> outline — or a dark-hued pictorial —
/// renders invisibly on one of the two themes (the AppleMusic mark vanished in dark mode,
/// 2026-08-22). The store's icon language (a colored mark on a brand-hue <c>rx=5</c> plate with
/// white detail) is exactly the form that is legible on BOTH grounds, so rather than auditing every
/// authored icon forever, the renderer guarantees the shape: an icon that already paints a
/// full-bleed plate passes through untouched, and one that does not is wrapped — plate in a hue
/// derived deterministically from the markup itself, <c>currentColor</c> recolored to white so the
/// glyph reads on the plate.</para>
///
/// <para>Pure and total: string in, string out, no services, no randomness — the hue is a stable
/// FNV-1a hash over the markup, so the same icon gets the same plate on every render, every
/// circuit, and in the browser tab (<see cref="MeshNodeImageHelper.IconLinkFor"/>). Thread
/// identicons (<c>ThreadIconGenerator</c>) already open with a full-bleed rect and pass through
/// unchanged.</para>
/// </summary>
public static partial class IconBackplate
{
    /// <summary>
    /// The plate hues a generated backplate draws from — mid-tone brand hues on which white detail
    /// stays legible, matching the palette the authored store marks already use. Order matters:
    /// <see cref="HueFor"/> indexes into it by hash, so reordering re-colors every generated plate.
    /// </summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#4338ca", // indigo
        "#1f6feb", // blue
        "#0e7490", // cyan
        "#0f766e", // teal
        "#15803d", // green
        "#b45309", // amber
        "#b91c1c", // red
        "#be185d", // pink
        "#7c3aed", // violet
        "#334155", // slate
    ];

    /// <summary>
    /// 🚨 THE OVERSTEER: the attribute an authored icon sets on its root <c>&lt;svg&gt;</c> to declare
    /// itself an OFFICIAL third-party mark — <c>data-mw-mark="official"</c>.
    ///
    /// <para>The default policy recolors <c>currentColor</c> to white and drops the glyph on a plate
    /// in a hash-derived hue. For a house icon that is exactly right. For a vendor's registered mark
    /// it is exactly wrong: every brand guideline these packages are nominatively invoking (we ship
    /// API clients to those services) forbids recoloring the mark or placing it on an arbitrary
    /// colored ground. An authored mark had no way to say so, so this is that way.</para>
    ///
    /// <para>It is a marker in the MARKUP rather than a field on the node deliberately: an icon
    /// travels as one opaque string through four different forms, so the declaration has to ride
    /// along with it — no service to thread, no plumbing to add, and it survives serialization,
    /// copy, export and re-import intact.</para>
    /// </summary>
    public const string OfficialMarkAttribute = "data-mw-mark";

    /// <summary>The value of <see cref="OfficialMarkAttribute"/> that claims official-mark treatment.</summary>
    public const string OfficialMarkValue = "official";

    /// <summary>
    /// The plate an official mark gets: white, not a hue from <see cref="Palette"/>.
    ///
    /// <para>🚨 An official mark is NOT passed through bare, and that is not a compromise — it is the
    /// same defect the generated plate exists to prevent. The OpenAI mark is near-black
    /// (<c>#111827</c>); rendered plateless it vanishes on the dark theme exactly as the AppleMusic
    /// mark did (2026-08-22). A white plate is also what the guidelines themselves prescribe — the
    /// dark mark on a light ground, with clear space — so this keeps the mark byte-identical AND
    /// legible on both themes, rather than trading one for the other.</para>
    /// </summary>
    public const string OfficialPlate = "#ffffff";

    /// <summary>The corner radius of a generated plate on the canonical 24-unit canvas.</summary>
    public const int CornerRadius = 5;

    /// <summary>The inset of the original glyph inside a generated plate, on the 24-unit canvas.</summary>
    public const int GlyphInset = 3;

    /// <summary>
    /// A full-bleed shape must cover at least this fraction of the canvas (per axis) to count as a
    /// backplate — an ornamental rect in a corner is glyph detail, not a plate.
    /// </summary>
    public const double CoverageThreshold = 0.9;

    /// <summary>
    /// The plate hue for <paramref name="seed"/> — a stable FNV-1a hash over the string, indexed
    /// into <see cref="Palette"/>. Deterministic so an icon keeps its hue across renders, circuits
    /// and deploys; seeded by the markup itself so no caller has to thread a node identity through.
    /// </summary>
    public static string HueFor(string seed)
    {
        // FNV-1a, 32-bit — tiny, allocation-free, and stable across runtimes (string.GetHashCode is
        // deliberately randomized per process and must never leak into rendered output).
        var hash = 2166136261u;
        foreach (var ch in seed)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return Palette[(int)(hash % (uint)Palette.Count)];
    }

    /// <summary>
    /// Whether <paramref name="svg"/> declares itself an official third-party mark — its ROOT
    /// <c>&lt;svg&gt;</c> carries <c>data-mw-mark="official"</c>. Only the root counts: a nested
    /// element could otherwise smuggle the claim in from arbitrary authored content.
    /// </summary>
    public static bool IsOfficialMark(string? svg)
    {
        if (string.IsNullOrWhiteSpace(svg))
            return false;
        var open = RootTag().Match(svg);
        return open.Success
               && string.Equals(
                   AttrOf(open.Groups["attrs"].Value, OfficialMarkAttribute),
                   OfficialMarkValue,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="svg"/> already paints its own full-bleed backplate: its first
    /// drawable element is a rect (or circle) covering at least <see cref="CoverageThreshold"/> of
    /// the canvas with a real fill. Measured against the icon's OWN canvas (viewBox, else
    /// width/height, else 24), so a 32- or 100-unit identicon is judged on its own scale.
    /// </summary>
    public static bool HasBackplate(string svg)
    {
        if (string.IsNullOrWhiteSpace(svg))
            return false;
        var (canvasW, canvasH) = CanvasOf(svg);
        var first = FirstDrawable().Match(svg);
        if (!first.Success)
            return false;
        var tag = first.Groups["tag"].Value;
        var attrs = first.Groups["attrs"].Value;
        var fill = AttrOf(attrs, "fill");
        if (fill is "none" or "transparent")
            return false;
        if (tag == "rect")
        {
            var w = NumberOf(attrs, "width", canvasW);
            var h = NumberOf(attrs, "height", canvasH);
            var x = NumberOf(attrs, "x", 0);
            var y = NumberOf(attrs, "y", 0);
            return w >= canvasW * CoverageThreshold
                   && h >= canvasH * CoverageThreshold
                   && x <= canvasW * (1 - CoverageThreshold)
                   && y <= canvasH * (1 - CoverageThreshold);
        }
        if (tag == "circle")
        {
            var r = NumberOf(attrs, "r", 0);
            return r >= Math.Min(canvasW, canvasH) * CoverageThreshold / 2;
        }
        return false;
    }

    /// <summary>
    /// The one entry point: <paramref name="svg"/> unchanged when it is not inline svg or already
    /// carries a plate; otherwise the same glyph on a generated <c>rx=5</c> plate. The original
    /// markup becomes a nested <c>&lt;svg&gt;</c> inset by <see cref="GlyphInset"/> — nesting keeps
    /// the original viewBox math intact for ANY canvas size (20, 24, 48, 100 are all authored in
    /// the wild), so no coordinate rewriting can corrupt a path. <c>currentColor</c> is recolored
    /// to white: on a plate it must read as detail, not inherit the surrounding text color.
    /// </summary>
    public static string Ensure(string? svg)
    {
        if (string.IsNullOrWhiteSpace(svg) || !svg.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return svg ?? "";
        if (HasBackplate(svg))
            return svg;

        // An official mark keeps its own colors on a white plate; a house icon is recolored to white
        // on a hash-derived hue. Both are plated — only the ground and the recolor differ.
        var official = IsOfficialMark(svg);
        var hue = official ? OfficialPlate : HueFor(svg);
        var inner = official
            ? svg
            : svg.Replace("currentColor", "#fff", StringComparison.OrdinalIgnoreCase);
        inner = InsetRoot(inner);
        // The wrapper re-declares the claim, so "is this an official mark" survives plating and a
        // caller downstream of Ensure reads the same answer as one upstream of it.
        var claim = official ? $" {OfficialMarkAttribute}='{OfficialMarkValue}'" : "";
        return $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'{claim}>"
               + $"<rect width='24' height='24' rx='{CornerRadius}' fill='{hue}' stroke='none'/>"
               + inner
               + "</svg>";
    }

    /// <summary>
    /// Rewrites the root <c>&lt;svg&gt;</c> tag of <paramref name="svg"/> so it nests inside the
    /// 24-unit plate: its own x/y/width/height (if any) are dropped — they would size the nested
    /// svg absolutely and defeat the inset — and replaced with the plate-relative box; a missing
    /// viewBox is synthesized from the authored width/height (else the canonical 24) so the glyph
    /// scales instead of clipping.
    /// </summary>
    private static string InsetRoot(string svg)
    {
        var open = RootTag().Match(svg);
        if (!open.Success)
            return svg;
        var attrs = open.Groups["attrs"].Value;
        var hadViewBox = AttrOf(attrs, "viewBox") is not null;
        var (w, h) = CanvasOf(svg);

        // Drop the attributes the wrapper owns; keep everything else (xmlns, fill, stroke, …).
        var kept = SizingAttr().Replace(attrs, "");
        var inset = GlyphInset;
        var span = 24 - 2 * inset;
        var viewBox = hadViewBox
            ? ""
            : $" viewBox='0 0 {Fmt(w)} {Fmt(h)}'";
        return svg[..open.Index]
               + $"<svg x='{inset}' y='{inset}' width='{span}' height='{span}'{viewBox}{kept}>"
               + svg[(open.Index + open.Length)..];
    }

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The canvas size of <paramref name="svg"/>: viewBox width/height, else the root's
    /// width/height attributes, else the canonical 24×24.</summary>
    private static (double Width, double Height) CanvasOf(string svg)
    {
        var open = RootTag().Match(svg);
        if (!open.Success)
            return (24, 24);
        var attrs = open.Groups["attrs"].Value;
        var viewBox = AttrOf(attrs, "viewBox");
        if (viewBox is not null)
        {
            var parts = viewBox.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vw)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vh)
                && vw > 0 && vh > 0)
                return (vw, vh);
        }
        var w = NumberOf(attrs, "width", 24);
        var h = NumberOf(attrs, "height", 24);
        return (w > 0 ? w : 24, h > 0 ? h : 24);
    }

    /// <summary>One attribute's value out of a tag's attribute text, quote-style agnostic. The
    /// lookbehind keeps a short name from matching inside a longer one — <c>x</c> must not read
    /// <c>rx="16"</c>, nor <c>width</c> read <c>stroke-width</c>.</summary>
    private static string? AttrOf(string attrs, string name)
    {
        var match = Regex.Match(
            attrs,
            @"(?<![-\w])" + name + @"\s*=\s*([""'])(?<v>.*?)\1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["v"].Value.Trim() : null;
    }

    /// <summary>A numeric attribute, tolerating <c>%</c> (relative to <paramref name="canvas"/>).</summary>
    private static double NumberOf(string attrs, string name, double fallback, double? canvas = null)
    {
        var raw = AttrOf(attrs, name);
        if (raw is null)
            return fallback;
        if (raw.EndsWith('%')
            && double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return (canvas ?? fallback) * pct / 100;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    [GeneratedRegex(@"<svg\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RootTag();

    [GeneratedRegex(
        @"<(?<tag>rect|circle|ellipse|path|polygon|polyline|line|text)\b(?<attrs>[^>]*?)/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirstDrawable();

    [GeneratedRegex(
        @"\s+(x|y|width|height)\s*=\s*([""']).*?\2",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizingAttr();
}
