using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// One measure compared across two sides.
///
/// <para>Both sides are NULLABLE on purpose, and null does not mean zero: it means the measure does
/// not exist on that side. The view says so in words instead of drawing a zero-length bar that
/// reads as "we looked and it was nothing".</para>
/// </summary>
/// <param name="Label">The measure's name (e.g. "Paid", "Outstanding").</param>
/// <param name="Left">The left side's value, or null when the measure is absent there.</param>
/// <param name="Right">The right side's value, or null when the measure is absent there.</param>
public record ComparisonPair(string Label, double? Left, double? Right);

/// <summary>
/// One pair's bars sized against the shared scale, in percent — null where the side is absent.
/// </summary>
/// <param name="Pair">The pair this row was computed for.</param>
/// <param name="LeftPercent">The left bar's length as a percentage of the scale, or null when absent.</param>
/// <param name="RightPercent">The right bar's length as a percentage of the scale, or null when absent.</param>
public record ComparisonPairLayout(ComparisonPair Pair, double? LeftPercent, double? RightPercent);

/// <summary>
/// The rows of a comparison, all sized against ONE scale so the eye reads the differences directly.
/// </summary>
/// <param name="Max">The scale's maximum — the largest value present on either side.</param>
/// <param name="Rows">The pairs in input order, each with its bar lengths.</param>
public record ComparisonBarsLayout(double Max, ImmutableList<ComparisonPairLayout> Rows);

/// <summary>
/// Paired bars: two series per measure on ONE shared scale, so the difference between the sides is
/// read directly off the lengths rather than inferred from two independently-scaled charts.
///
/// <para>Its point of view is the absent side. A measure that does not exist on one side renders as
/// words ("not on this side"), never as a zero-length bar — the distinction between "nothing" and
/// "we do not have this" is the whole reason to put a comparison on screen.</para>
///
/// <para>Composable like any other leaf control — drop it into a <c>Controls.Stack</c> or a
/// <c>Controls.LayoutGrid</c> cell.</para>
/// </summary>
/// <param name="Pairs">The measures, or a data-binding reference resolving to them.</param>
public record ComparisonBarsControl(object Pairs)
    : UiControl<ComparisonBarsControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Already-localized name of the left series (e.g. "reported"). Optional.</summary>
    public object? LeftLegend { get; init; }

    /// <summary>Already-localized name of the right series (e.g. "ours"). Optional.</summary>
    public object? RightLegend { get; init; }

    /// <summary>
    /// What to say where a side has no value. Null renders the localized default
    /// ("not on this side"); a domain with its own phrasing passes its own already-localized text.
    /// </summary>
    public object? AbsentText { get; init; }

    /// <summary>
    /// .NET numeric format for the values beside the bars (e.g. <c>"N0"</c>, <c>"C2"</c>).
    /// Null renders the view default (<c>N0</c>).
    /// </summary>
    public object? Format { get; init; }

    /// <summary>Returns a copy comparing <paramref name="pairs"/> instead.</summary>
    /// <param name="pairs">The measures, or a data-binding reference resolving to them.</param>
    /// <returns>A new <see cref="ComparisonBarsControl"/> with the specified pairs.</returns>
    public ComparisonBarsControl WithPairs(object pairs) => this with { Pairs = pairs };

    /// <summary>Returns a copy naming the two series.</summary>
    /// <param name="left">Already-localized name of the left series.</param>
    /// <param name="right">Already-localized name of the right series.</param>
    /// <returns>A new <see cref="ComparisonBarsControl"/> with the specified legends.</returns>
    public ComparisonBarsControl WithLegends(object left, object right) =>
        this with { LeftLegend = left, RightLegend = right };

    /// <summary>Returns a copy saying <paramref name="absentText"/> where a side has no value.</summary>
    /// <param name="absentText">Already-localized text for an absent side.</param>
    /// <returns>A new <see cref="ComparisonBarsControl"/> with the specified absent text.</returns>
    public ComparisonBarsControl WithAbsentText(object absentText) => this with { AbsentText = absentText };

    /// <summary>Returns a copy whose values use <paramref name="format"/>.</summary>
    /// <param name="format">A .NET numeric format string, e.g. <c>"N0"</c>.</param>
    /// <returns>A new <see cref="ComparisonBarsControl"/> with the specified value format.</returns>
    public ComparisonBarsControl WithFormat(object format) => this with { Format = format };

    /// <summary>
    /// Sizes every bar against the single largest value present. Pure — the comparison's whole
    /// arithmetic, kept out of the views.
    ///
    /// <para>An absent side stays <c>null</c> rather than becoming 0: the view must be able to tell
    /// the two apart. Returns <c>null</c> when no side of any pair carries a positive value — there
    /// is no scale to draw against, and the view says so.</para>
    /// </summary>
    /// <param name="pairs">The measures to size.</param>
    /// <returns>The resolved bar lengths, or <c>null</c> when there is no positive scale.</returns>
    public static ComparisonBarsLayout? Layout(IEnumerable<ComparisonPair>? pairs)
    {
        var rows = (pairs ?? []).ToImmutableList();
        if (rows.IsEmpty)
            return null;

        var max = rows.Max(p => Math.Max(p.Left ?? 0, p.Right ?? 0));
        if (max <= 0)
            return null;

        return new ComparisonBarsLayout(
            max,
            rows.Select(p => new ComparisonPairLayout(p, Percent(p.Left, max), Percent(p.Right, max)))
                .ToImmutableList());
    }

    // An absent side is null, never 0. A present-but-tiny value keeps a visible sliver so the row
    // still reads as "there is a figure here, and it is small".
    private static double? Percent(double? value, double max) =>
        value is null ? null : Math.Clamp(Math.Max(value.Value / max, 0.005), 0, 1) * 100;
}
