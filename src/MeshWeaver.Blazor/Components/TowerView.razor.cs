using System.Collections.Immutable;
using System.Globalization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Renders a <see cref="TowerControl"/> as a vertical stack of bands over its retention.
///
/// <para>Deliberately CSS, not SVG: percentage-positioned blocks inherit the design tokens (so the
/// tower is theme-correct without a palette of its own), stay readable when the container is
/// narrow, and keep every label as real selectable text rather than a glyph run. The arithmetic is
/// not here — <see cref="TowerControl.Layout"/> owns it, so this view is a projection.</para>
/// </summary>
public partial class TowerView
{
    private TowerLayout? Layout { get; set; }
    private string? Currency { get; set; }
    private string RetentionLabel { get; set; } = string.Empty;
    private string Height { get; set; } = DefaultHeight;
    private string? Format { get; set; }

    private const string DefaultHeight = "420px";

    private double RetentionPercent =>
        Layout is null or { Top: <= 0 } ? 0 : Layout.Retention / Layout.Top * 100;

    /// <summary>
    /// The amounts worth labelling on the axis: zero, the top, and every band's attachment. Ticks
    /// closer together than a hair's breadth of the axis collapse into one so a tightly-stacked
    /// tower does not print its labels on top of each other.
    /// </summary>
    private ImmutableList<(double Percent, string Label)> Ticks
    {
        get
        {
            if (Layout is null or { Top: <= 0 })
                return [];

            var amounts = Layout.Bands
                .Select(b => b.Band.Attachment)
                .Append(0d)
                .Append(Layout.Top)
                .Where(a => a >= 0 && a <= Layout.Top)
                .Distinct()
                .OrderBy(a => a);

            var ticks = ImmutableList.CreateBuilder<(double, string)>();
            double? previous = null;
            foreach (var amount in amounts)
            {
                var percent = amount / Layout.Top * 100;
                if (previous is { } p && percent - p < 4)
                    continue;
                previous = percent;
                ticks.Add((percent, AnalysisFormatting.Format(amount, Format, Access)));
            }
            return ticks.ToImmutable();
        }
    }

    private static double SharePercent(TowerBand band) => Math.Clamp(band.Share, 0, 1) * 100;

    // CSS lengths are culture-invariant: a German thread rendering "12,5%" would be an invalid
    // declaration the browser drops, silently collapsing the band to zero height.
    private static string Css(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override void BindData()
    {
        base.BindData();
        if (ViewModel is null)
            return;

        DataBind(ViewModel.Bands, x => x.Layout,
            (value, _) => TowerControl.Layout(AnalysisRows.Resolve<TowerBand>(value, Hub.JsonSerializerOptions)));
        DataBind(ViewModel.Currency, x => x.Currency);
        DataBind(ViewModel.RetentionLabel, x => x.RetentionLabel,
            defaultValue: Access.Localize("analysis.tower.retained"));
        DataBind(ViewModel.Height, x => x.Height, defaultValue: DefaultHeight);
        DataBind(ViewModel.Format, x => x.Format);
    }
}
