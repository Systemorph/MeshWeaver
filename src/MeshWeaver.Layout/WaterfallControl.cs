using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// What a <see cref="WaterfallStep"/> is — an OPEN vocabulary of string constants, never an enum
/// (policy <c>open-vocabulary-string-constants</c>): a renderer that meets a kind it does not know
/// draws it as a <see cref="Delta"/>.
/// </summary>
public static class WaterfallStepKind
{
    /// <summary>
    /// A level that stands on the axis: the opening amount, a subtotal, the closing result. Its
    /// <see cref="WaterfallStep.Amount"/> is the LEVEL itself, and the running level continues from it.
    /// </summary>
    public const string Total = "total";

    /// <summary>
    /// A change that hangs from the running level: a charge (negative) or a contribution (positive).
    /// Its <see cref="WaterfallStep.Amount"/> is SIGNED and moves the running level by that much.
    /// </summary>
    public const string Delta = "delta";
}

/// <summary>
/// The tone a waterfall bar is drawn in — an open vocabulary the renderers map onto design tokens.
/// </summary>
public static class WaterfallTone
{
    /// <summary>An opening amount or a subtotal.</summary>
    public const string Total = "total";

    /// <summary>A delta that raises the running level.</summary>
    public const string Increase = "increase";

    /// <summary>A delta that lowers the running level.</summary>
    public const string Decrease = "decrease";

    /// <summary>The closing total, at or above zero.</summary>
    public const string ResultPositive = "result-positive";

    /// <summary>The closing total, below zero.</summary>
    public const string ResultNegative = "result-negative";
}

/// <summary>
/// One bar of a <see cref="WaterfallControl"/>.
/// </summary>
/// <param name="Label">The bar's name under the axis (already localized, e.g. "Expected loss").</param>
/// <param name="Amount">
/// For a <see cref="WaterfallStepKind.Total"/>, the level itself; for a
/// <see cref="WaterfallStepKind.Delta"/>, the signed change of the running level.
/// </param>
/// <param name="Kind">A <see cref="WaterfallStepKind"/> constant; defaults to a delta.</param>
public record WaterfallStep(string Label, double Amount, string Kind = WaterfallStepKind.Delta)
{
    /// <summary>An opening amount, subtotal or result — a bar standing on the axis.</summary>
    /// <param name="label">The bar's name.</param>
    /// <param name="amount">The level.</param>
    /// <returns>A new total step.</returns>
    public static WaterfallStep Total(string label, double amount) => new(label, amount, WaterfallStepKind.Total);

    /// <summary>A signed change hanging from the running level.</summary>
    /// <param name="label">The bar's name.</param>
    /// <param name="amount">The signed change.</param>
    /// <returns>A new delta step.</returns>
    public static WaterfallStep Delta(string label, double amount) => new(label, amount, WaterfallStepKind.Delta);
}

/// <summary>
/// One step resolved onto the waterfall's axis, in percent of the axis span measured from its
/// bottom — the pure geometry <see cref="WaterfallControl.Layout"/> hands a view.
/// </summary>
/// <param name="Step">The step this bar was computed for.</param>
/// <param name="From">The level the bar starts at (0 for a total).</param>
/// <param name="To">The level the bar ends at — the running level after this step.</param>
/// <param name="BottomPercent">The lower edge of the bar.</param>
/// <param name="HeightPercent">The bar's height; a non-zero amount keeps a visible sliver.</param>
/// <param name="LevelPercent">The running level after this step — where the connector to the next bar runs.</param>
/// <param name="Tone">A <see cref="WaterfallTone"/> constant.</param>
/// <param name="Signed">True when the value label carries its sign (a delta), false for a level.</param>
public record WaterfallBarLayout(
    WaterfallStep Step,
    double From,
    double To,
    double BottomPercent,
    double HeightPercent,
    double LevelPercent,
    string Tone,
    bool Signed);

/// <summary>
/// The whole waterfall on one axis.
/// </summary>
/// <param name="Min">The axis bottom — the lowest level reached, never above zero.</param>
/// <param name="Max">The axis top — the highest level reached, never below zero.</param>
/// <param name="ZeroPercent">Where zero sits on the axis, so the view can draw the baseline.</param>
/// <param name="Bars">The steps in input order, each with its geometry.</param>
public record WaterfallLayout(double Min, double Max, double ZeroPercent, ImmutableList<WaterfallBarLayout> Bars);

/// <summary>
/// A waterfall (bridge) chart: an opening amount, the deltas that hang from the running level, and
/// the result standing on the axis again — premium → losses → costs → technical result, or any other
/// "from here to there, by these steps" figure.
///
/// <para>The arithmetic is framework-owned: <see cref="Layout"/> is pure, returns every bar's position
/// as a percentage of the axis, and is pinned by unit tests, so the renderers are projections with no
/// arithmetic of their own. A total's <see cref="WaterfallStep.Amount"/> is the LEVEL, a delta's is the
/// signed CHANGE — the caller states which, the control never guesses.</para>
///
/// <para>Composable like any other leaf control — drop it into a <c>Controls.Stack</c> or a
/// <c>Controls.LayoutGrid</c> cell.</para>
/// </summary>
/// <param name="Steps">The steps, or a data-binding reference resolving to them.</param>
public record WaterfallControl(object Steps)
    : UiControl<WaterfallControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The unit the amounts are denominated in (a currency code, "units", …). Optional.</summary>
    public object? Currency { get; init; }

    /// <summary>
    /// .NET numeric format for the value labels (e.g. <c>"N0"</c>). Null renders the view default
    /// (<c>N0</c>).
    /// </summary>
    public object? Format { get; init; }

    /// <summary>The plot height as a CSS length. Null renders the view default (<c>280px</c>).</summary>
    public object? Height { get; init; }

    /// <summary>Returns a copy drawing <paramref name="steps"/> instead.</summary>
    /// <param name="steps">The steps, or a data-binding reference resolving to them.</param>
    /// <returns>A new <see cref="WaterfallControl"/> with the specified steps.</returns>
    public WaterfallControl WithSteps(object steps) => this with { Steps = steps };

    /// <summary>Returns a copy denominated in <paramref name="currency"/>.</summary>
    /// <param name="currency">The unit label shown beside the plot.</param>
    /// <returns>A new <see cref="WaterfallControl"/> with the specified currency.</returns>
    public WaterfallControl WithCurrency(object currency) => this with { Currency = currency };

    /// <summary>Returns a copy whose value labels use <paramref name="format"/>.</summary>
    /// <param name="format">A .NET numeric format string, e.g. <c>"N0"</c>.</param>
    /// <returns>A new <see cref="WaterfallControl"/> with the specified format.</returns>
    public WaterfallControl WithFormat(object format) => this with { Format = format };

    /// <summary>Returns a copy plotted at <paramref name="height"/>.</summary>
    /// <param name="height">A CSS length, e.g. <c>"320px"</c>.</param>
    /// <returns>A new <see cref="WaterfallControl"/> with the specified plot height.</returns>
    public WaterfallControl WithHeight(object height) => this with { Height = height };

    /// <summary>
    /// Resolves <paramref name="steps"/> onto one axis. A total stands on zero and RESETS the running
    /// level to its amount; a delta hangs from the running level and moves it by its signed amount.
    /// The axis spans every level reached and always includes zero. The last step, when it is a
    /// total, is the RESULT and is toned by its sign. Pure — the waterfall's whole arithmetic.
    ///
    /// <para>Returns <c>null</c> when there is nothing honest to draw: no steps, a non-finite amount,
    /// or every level at zero. A view says so in a sentence rather than drawing an empty frame.</para>
    /// </summary>
    /// <param name="steps">The steps to place.</param>
    /// <returns>The resolved geometry, or <c>null</c> when the input cannot be drawn.</returns>
    public static WaterfallLayout? Layout(IEnumerable<WaterfallStep>? steps)
    {
        var list = (steps ?? []).ToImmutableList();
        if (list.IsEmpty || list.Any(s => !double.IsFinite(s.Amount)))
            return null;

        var spans = new List<(double From, double To)>(list.Count);
        var level = 0d;
        foreach (var step in list)
        {
            if (IsTotal(step))
            {
                spans.Add((0, step.Amount));
                level = step.Amount;
            }
            else
            {
                spans.Add((level, level + step.Amount));
                level += step.Amount;
            }
        }

        var min = Math.Min(0, spans.Min(s => Math.Min(s.From, s.To)));
        var max = Math.Max(0, spans.Max(s => Math.Max(s.From, s.To)));
        var span = max - min;
        if (span <= 0)
            return null;

        double Percent(double v) => Math.Clamp((v - min) / span, 0, 1) * 100;

        var bars = list.Select((step, i) =>
        {
            var (from, to) = spans[i];
            var bottom = Percent(Math.Min(from, to));
            var height = Percent(Math.Max(from, to)) - bottom;
            // A present-but-tiny amount keeps a visible sliver so the bar still reads as "there is a
            // figure here, and it is small"; an exact zero draws nothing.
            if (step.Amount != 0)
            {
                height = Math.Max(height, 0.5);
                bottom = Math.Min(bottom, 100 - height);
            }
            var total = IsTotal(step);
            var tone = total
                ? i == list.Count - 1 && list.Count > 1
                    ? step.Amount >= 0 ? WaterfallTone.ResultPositive : WaterfallTone.ResultNegative
                    : WaterfallTone.Total
                : step.Amount >= 0 ? WaterfallTone.Increase : WaterfallTone.Decrease;
            return new WaterfallBarLayout(step, from, to, bottom, height, Percent(to), tone, Signed: !total);
        }).ToImmutableList();

        return new WaterfallLayout(min, max, Percent(0), bars);
    }

    private static bool IsTotal(WaterfallStep step) =>
        string.Equals(step.Kind, WaterfallStepKind.Total, StringComparison.OrdinalIgnoreCase);
}
