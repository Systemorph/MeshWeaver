using System.Globalization;

namespace MeshWeaver.Markdown.Export.Email;

/// <summary>
/// The inline-CSS vocabulary of the email renderer. Constants, not a cache — every value is a
/// compile-time literal read at render time and never written.
///
/// <para><b>Why each rule is what it is.</b> A mail client is not a browser: Outlook on Windows
/// renders through the WORD engine. That single fact drives the whole design —</para>
/// <list type="bullet">
/// <item><description>No <c>&lt;style&gt;</c> block and no external stylesheet: Outlook.com and
/// Gmail strip or rewrite head styles, so styling has to ride inline on each element.</description></item>
/// <item><description>No flexbox and no CSS grid: Word supports neither, so any multi-column
/// layout is a <c>&lt;table&gt;</c> — which is why the card grid below is a table.</description></item>
/// <item><description>No inline SVG: Word shows it as a broken-image box. The sanitizer removes
/// SVG entirely rather than shipping a broken glyph.</description></item>
/// <item><description>Explicit <c>cellpadding</c>/<c>cellspacing</c>/<c>border</c> attributes
/// alongside the CSS, because Word honours the attributes more reliably than the properties.</description></item>
/// </list>
/// </summary>
public static class EmailStyles
{
    /// <summary>Body font stack — system fonts only; a webfont cannot load in mail.</summary>
    public const string FontStack =
        "-apple-system, 'Segoe UI', Helvetica, Arial, sans-serif";

    /// <summary>Primary text colour.</summary>
    public const string TextColor = "#0f172a";

    /// <summary>Secondary/subdued text colour (card descriptions).</summary>
    public const string MutedColor = "#475569";

    /// <summary>Link colour.</summary>
    public const string LinkColor = "#2563eb";

    /// <summary>Card border colour.</summary>
    public const string BorderColor = "#dbe4ee";

    /// <summary>Card background.</summary>
    public const string CardBackground = "#f8fafc";

    /// <summary>The document body style (page ground).</summary>
    public const string Body = "margin:0;padding:0;background:#ffffff";

    /// <summary>
    /// The content wrapper: bounded width, centred, with the base typography every nested
    /// element inherits.
    /// </summary>
    public static string Wrapper(int maxWidthPx) =>
        string.Create(CultureInfo.InvariantCulture,
            $"max-width:{maxWidthPx}px;margin:0 auto;padding:24px 28px;font-family:{FontStack};"
            + $"font-size:15px;line-height:1.62;color:{TextColor}");

    /// <summary>A layout table: full width, fixed layout so columns divide evenly.</summary>
    public const string GridTable = "margin:12px 0 24px 0;table-layout:fixed";

    /// <summary>The padding cell wrapping one card, leaving the gutter between columns.</summary>
    public const string CardCell = "padding:0 8px 16px 0";

    /// <summary>The card frame: border, radius, tint, and the FIXED height that aligns rows.</summary>
    public static string CardFrame(int heightPx) =>
        string.Create(CultureInfo.InvariantCulture,
            $"border:1px solid {BorderColor};border-radius:10px;background:{CardBackground};height:{heightPx}px");

    /// <summary>The card's inner cell.</summary>
    public static string CardBody(int heightPx) =>
        string.Create(CultureInfo.InvariantCulture, $"padding:13px 15px;height:{heightPx}px");

    /// <summary>The card title link.</summary>
    public static string CardTitle =>
        $"color:{TextColor};text-decoration:none;font-weight:700;font-size:15px";

    /// <summary>The card description line.</summary>
    public static string CardDescription =>
        $"color:{MutedColor};font-size:13px;line-height:1.45;margin-top:5px";

    /// <summary>The card's trailing link line.</summary>
    public static string CardLink =>
        $"color:{LinkColor};text-decoration:none;font-size:12px;font-weight:700";

    /// <summary>The card's small leading image (icon), when the target supplied a raster one.</summary>
    public const string CardImage = "display:block;border:0;outline:none;text-decoration:none";
}
