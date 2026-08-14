using System.Globalization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Analysis;

/// <summary>
/// Renders a <see cref="ComparisonBarsControl"/> as two bars per measure on one shared scale.
///
/// <para>The view's one job beyond layout is the absent side: where a value is null it prints
/// <see cref="AbsentText"/> instead of a bar, because a zero-length bar and "we do not hold this"
/// look identical and mean opposite things. The scale itself is
/// <see cref="ComparisonBarsControl.Layout"/>'s.</para>
/// </summary>
public partial class ComparisonBarsView
{
    private ComparisonBarsLayout? Layout { get; set; }
    private string? LeftLegend { get; set; }
    private string? RightLegend { get; set; }
    private string AbsentText { get; set; } = string.Empty;
    private string? Format { get; set; }

    private string Formatted(double value) => AnalysisFormatting.Format(value, Format, Access);

    // CSS lengths are culture-invariant — see TowerView.Css.
    private static string Css(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override void BindData()
    {
        base.BindData();
        if (ViewModel is null)
            return;

        DataBind(ViewModel.Pairs, x => x.Layout,
            (value, _) => ComparisonBarsControl.Layout(
                AnalysisRows.Resolve<ComparisonPair>(value, Hub.JsonSerializerOptions)));
        DataBind(ViewModel.LeftLegend, x => x.LeftLegend);
        DataBind(ViewModel.RightLegend, x => x.RightLegend);
        DataBind(ViewModel.AbsentText, x => x.AbsentText,
            defaultValue: Access.Localize("analysis.comparison.absent"));
        DataBind(ViewModel.Format, x => x.Format);
    }
}
