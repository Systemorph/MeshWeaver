using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// One band of a <see cref="TowerControl"/> — a cover of <c>Cover</c> in excess of
/// <c>Attachment</c>, of which <c>Share</c> is taken.
/// </summary>
/// <param name="Label">The band's headline (e.g. "MTPL XL Layer 1").</param>
/// <param name="Terms">The terms line under the headline (e.g. "7m xs 3m · 7.8% RoL · 50% share").</param>
/// <param name="Attachment">Where the band starts paying. Amounts below it fall to the retention.</param>
/// <param name="Cover">The band's cover above <paramref name="Attachment"/>; the band's height.</param>
/// <param name="Share">
/// The taken share of the band, 0..1 — drawn as the solid portion of the band's width, the rest
/// outlined. Defaults to 1 (the whole band).
/// </param>
/// <param name="Href">Navigation target for the band; null renders it unlinked.</param>
public record TowerBand(
    string Label,
    string Terms,
    double Attachment,
    double Cover,
    double Share = 1,
    string? Href = null);

/// <summary>
/// One band positioned on the tower's axis, in percent of the tower's full height — the pure
/// geometry <see cref="TowerControl.Layout"/> hands to a view so the view itself stays a
/// projection with no arithmetic of its own.
/// </summary>
/// <param name="Band">The band this position was computed for.</param>
/// <param name="BottomPercent">The band's attachment as a percentage of the tower's exhaustion point.</param>
/// <param name="HeightPercent">The band's cover as a percentage of the tower's exhaustion point.</param>
public record TowerBandLayout(TowerBand Band, double BottomPercent, double HeightPercent);

/// <summary>
/// The whole tower resolved onto one axis: where it tops out, how much sits below the lowest
/// attachment, and each band's place on it.
/// </summary>
/// <param name="Top">The highest exhaustion point across the bands — the top of the axis.</param>
/// <param name="Retention">
/// The lowest attachment: everything below the tower, which the view draws as the hatched base.
/// Zero when a band attaches at zero.
/// </param>
/// <param name="Bands">The bands, ordered by attachment, each with its position.</param>
public record TowerLayout(double Top, double Retention, ImmutableList<TowerBandLayout> Bands);

/// <summary>
/// The excess-of-loss tower drawn the way the domain draws it: a VERTICAL stack whose axis is the
/// amount, each band a block spanning attachment → exhaustion, consecutive bands sitting directly on
/// top of one another, and the retention as the base the tower stands on. The taken
/// <see cref="TowerBand.Share"/> is the solid portion of a band's width; a band with an
/// <see cref="TowerBand.Href"/> is a link.
///
/// <para>Composable like any other leaf control — drop it into a <c>Controls.Stack</c> or a
/// <c>Controls.LayoutGrid</c> cell.</para>
/// </summary>
/// <param name="Bands">The bands, or a data-binding reference resolving to them.</param>
public record TowerControl(object Bands)
    : UiControl<TowerControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The unit the axis is denominated in (a currency code, "units", …). Optional.</summary>
    public object? Currency { get; init; }

    /// <summary>
    /// What to call the base below the lowest attachment. Null renders the localized default
    /// ("retained"); domains with their own word for it (a cedent's retention, a deductible) pass
    /// their own already-localized text.
    /// </summary>
    public object? RetentionLabel { get; init; }

    /// <summary>The plot height as a CSS length. Null renders the view default (<c>420px</c>).</summary>
    public object? Height { get; init; }

    /// <summary>
    /// .NET numeric format for the axis ticks (e.g. <c>"N0"</c>, <c>"C0"</c>). Null renders the
    /// view default (<c>N0</c>).
    /// </summary>
    public object? Format { get; init; }

    /// <summary>Returns a copy drawing <paramref name="bands"/> instead.</summary>
    /// <param name="bands">The bands, or a data-binding reference resolving to them.</param>
    /// <returns>A new <see cref="TowerControl"/> with the specified bands.</returns>
    public TowerControl WithBands(object bands) => this with { Bands = bands };

    /// <summary>Returns a copy whose axis is denominated in <paramref name="currency"/>.</summary>
    /// <param name="currency">The unit label shown beside the axis.</param>
    /// <returns>A new <see cref="TowerControl"/> with the specified currency.</returns>
    public TowerControl WithCurrency(object currency) => this with { Currency = currency };

    /// <summary>Returns a copy labelling the base <paramref name="retentionLabel"/>.</summary>
    /// <param name="retentionLabel">Already-localized text for the base below the lowest attachment.</param>
    /// <returns>A new <see cref="TowerControl"/> with the specified retention label.</returns>
    public TowerControl WithRetentionLabel(object retentionLabel) => this with { RetentionLabel = retentionLabel };

    /// <summary>Returns a copy plotted at <paramref name="height"/>.</summary>
    /// <param name="height">A CSS length, e.g. <c>"520px"</c>.</param>
    /// <returns>A new <see cref="TowerControl"/> with the specified plot height.</returns>
    public TowerControl WithHeight(object height) => this with { Height = height };

    /// <summary>Returns a copy whose axis ticks use <paramref name="format"/>.</summary>
    /// <param name="format">A .NET numeric format string, e.g. <c>"N0"</c>.</param>
    /// <returns>A new <see cref="TowerControl"/> with the specified tick format.</returns>
    public TowerControl WithFormat(object format) => this with { Format = format };

    /// <summary>
    /// Resolves <paramref name="bands"/> onto one axis: the exhaustion point, the retention below
    /// the lowest attachment, and each band's position in percent of the axis. Pure — this is the
    /// tower's whole arithmetic, kept out of the views so both the Blazor and the JS renderer draw
    /// the same picture from the same numbers.
    ///
    /// <para>Returns <c>null</c> when there is nothing honest to draw: no bands at all, or no
    /// positive exhaustion point. A view renders a sentence saying so rather than an empty frame.</para>
    /// </summary>
    /// <param name="bands">The bands to place.</param>
    /// <returns>The resolved geometry, or <c>null</c> when the input cannot be drawn.</returns>
    public static TowerLayout? Layout(IEnumerable<TowerBand>? bands)
    {
        var ordered = (bands ?? []).OrderBy(b => b.Attachment).ThenBy(b => b.Cover).ToImmutableList();
        if (ordered.IsEmpty)
            return null;

        var top = ordered.Max(b => b.Attachment + b.Cover);
        if (top <= 0)
            return null;

        var retention = Math.Max(0, ordered.Min(b => b.Attachment));
        return new TowerLayout(
            top,
            retention,
            ordered
                .Select(b => new TowerBandLayout(
                    b,
                    Math.Clamp(b.Attachment / top, 0, 1) * 100,
                    Math.Clamp(b.Cover / top, 0, 1) * 100))
                .ToImmutableList());
    }

    /// <summary>
    /// The href a band navigates to, normalized to a site-root-relative path. A value that is
    /// already absolute (a scheme, a protocol-relative or a root-relative path) is left alone; a
    /// bare mesh path such as <c>Acme/Deal/Layer1</c> gains its leading slash. Null/blank stays
    /// null so the view renders the band unlinked.
    /// </summary>
    /// <param name="href">The band's raw href.</param>
    /// <returns>The navigable href, or <c>null</c>.</returns>
    public static string? NavigableHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;
        var trimmed = href.Trim();
        return trimmed.StartsWith('/') || trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : "/" + trimmed;
    }
}
