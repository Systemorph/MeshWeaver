namespace MeshWeaver.Markdown.Export.Html;

/// <summary>
/// Tuning for the email-safe HTML renderer. Every default here encodes a constraint of the
/// LOWEST common denominator among mail clients (Outlook desktop, which renders through Word),
/// not a stylistic preference — see <see cref="MarkupStyles"/> for the rationale per token.
/// </summary>
/// <param name="BaseUrl">
/// Absolute origin every relative link and image is rewritten against (e.g.
/// <c>https://memex.meshweaver.cloud</c>). A mail client has no page origin, so a relative
/// <c>/Some/Node</c> href is simply DEAD — absolutising is not cosmetic.
/// </param>
/// <param name="ContentWidthPx">Max body width; wider mail is unreadable in a preview pane.</param>
/// <param name="CardHeightPx">
/// Uniform card height. Cards sized by their own content produce ragged rows — a table row is
/// only as tall as its tallest cell, so a one-line card next to a three-line card leaves a gap.
/// Fixing the height makes every row line up.
/// </param>
/// <param name="CardDescriptionMaxChars">
/// Word-boundary clip for a card description, so text cannot overflow the fixed card height.
/// </param>
/// <param name="CardColumns">Cards per row in the card table (2 reads well at both widths).</param>
/// <param name="AreaSettleWindow">
/// How long a layout area must stop changing before its tree is snapshotted. A live area has no
/// completion signal — <c>OgCard</c> emits placeholder cards first and fills each one in as its
/// Open Graph fetch or node stream lands — so "settled" (quiescent for this long) is the only
/// honest definition of "done" when converting a live stream into a static document.
/// </param>
/// <param name="AreaTimeout">
/// Upper bound on one area's resolution. On expiry the area renders as whatever it last emitted
/// (a placeholder card grid), never as an empty div and never faulting the whole export.
/// </param>
public record DocumentHtmlOptions(
    string BaseUrl,
    int ContentWidthPx = 840,
    int CardHeightPx = 118,
    int CardDescriptionMaxChars = 108,
    int CardColumns = 2,
    TimeSpan? AreaSettleWindow = null,
    TimeSpan? AreaTimeout = null)
{
    /// <summary>The settle window, defaulted.</summary>
    public TimeSpan SettleWindow => AreaSettleWindow ?? TimeSpan.FromMilliseconds(750);

    /// <summary>The per-area timeout, defaulted.</summary>
    public TimeSpan Timeout => AreaTimeout ?? TimeSpan.FromSeconds(20);

    /// <summary>
    /// The base URL with any trailing slash removed, so concatenation never doubles it.
    /// </summary>
    public string NormalizedBaseUrl => (BaseUrl ?? string.Empty).TrimEnd('/');
}
