using System.Collections.Immutable;

namespace MeshWeaver.Layout.Chart;

/// <summary>
/// Represents a chart control with customizable series and configuration.
/// </summary>
public record ChartControl()
    : ContainerControl<ChartControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Gets or initializes the chart title.
    /// </summary>
    public object? Title { get; init; }

    /// <summary>
    /// Gets or initializes the chart subtitle.
    /// </summary>
    public object? Subtitle { get; init; }

    /// <summary>
    /// Gets or initializes whether to show the legend.
    /// </summary>
    public object? ShowLegend { get; init; }

    /// <summary>
    /// Gets or initializes the legend position.
    /// </summary>
    public object? LegendPosition { get; init; }

    /// <summary>
    /// Gets or initializes the labels for the X-axis categories.
    /// </summary>
    public object? Labels { get; init; }

    /// <summary>
    /// Gets the series in this chart.
    /// </summary>
    public object? Series { get; init; }

    /// <summary>
    /// Gets or initializes whether the chart is stacked.
    /// </summary>
    public object? IsStacked { get; init; }

    /// <summary>
    /// Gets or initializes whether to disable animation.
    /// </summary>
    public object? DisableAnimation { get; init; }

    /// <summary>
    /// Gets or initializes the width of the chart.
    /// </summary>
    public object? Width { get; init; }

    /// <summary>
    /// Gets or initializes the height of the chart.
    /// </summary>
    public object? Height { get; init; }

    /// <summary>
    /// Gets or initializes the angle for category axis labels (in degrees). Default is -45 to prevent overlap.
    /// </summary>
    public object? CategoryAxisLabelAngle { get; init; }

    /// <summary>
    /// Adds a series to the chart.
    /// </summary>
    public ChartControl WithSeries(ChartSeries series)
    {
        var currentSeries = Series as ImmutableList<ChartSeries> ?? ImmutableList<ChartSeries>.Empty;
        var newSeries = currentSeries.Add(series);
        var chart = this with { Series = newSeries };

        // Auto-enable legend if we have multiple series with labels
        if (chart.ShowLegend == null &&
            newSeries.Count > 1 &&
            newSeries.Any(s => s.Label is string label && !string.IsNullOrEmpty(label)))
        {
            chart = chart with { ShowLegend = true };
        }

        return chart;
    }

    /// <summary>
    /// Adds multiple series to the chart.
    /// </summary>
    public ChartControl WithSeries(params ChartSeries[] series)
    {
        var chart = this;
        foreach (var s in series)
        {
            chart = chart.WithSeries(s);
        }
        return chart;
    }

    /// <summary>
    /// Sets the chart title.
    /// </summary>
    public ChartControl WithTitle(object title) => this with { Title = title };

    /// <summary>
    /// Sets the chart subtitle.
    /// </summary>
    public ChartControl WithSubtitle(object subtitle) => this with { Subtitle = subtitle };

    /// <summary>
    /// Enables or disables the legend.
    /// </summary>
    public ChartControl WithLegend(object show) => this with { ShowLegend = show };

    /// <summary>
    /// Sets the legend position.
    /// </summary>
    public ChartControl WithLegendPosition(LegendPosition position) =>
        this with { ShowLegend = true, LegendPosition = position };

    /// <summary>
    /// Sets the legend position.
    /// </summary>
    public ChartControl WithLegendPosition(object position) =>
        this with { ShowLegend = true, LegendPosition = position };

    /// <summary>
    /// Sets the labels for the X-axis categories.
    /// </summary>
    public ChartControl WithLabels(params string[] labels) =>
        this with { Labels = labels.ToImmutableList() };

    /// <summary>
    /// Sets the labels for the X-axis categories.
    /// </summary>
    public ChartControl WithLabels(IEnumerable<string> labels) =>
        this with { Labels = labels.ToImmutableList() };

    /// <summary>
    /// Sets the labels for the X-axis categories.
    /// </summary>
    public ChartControl WithLabels(object labels) =>
        this with { Labels = labels };

    /// <summary>
    /// Makes the chart stacked.
    /// </summary>
    public ChartControl Stacked() => this with { IsStacked = true };

    /// <summary>
    /// Disables chart animations.
    /// </summary>
    public ChartControl WithoutAnimation() => this with { DisableAnimation = true };

    /// <summary>
    /// Sets the width of the chart.
    /// </summary>
    public ChartControl WithWidth(object width) => this with { Width = width };

    /// <summary>
    /// Sets the height of the chart.
    /// </summary>
    public ChartControl WithHeight(object height) => this with { Height = height };

    /// <summary>
    /// Sets the angle for category axis labels (in degrees). Use negative values for counter-clockwise rotation.
    /// </summary>
    public ChartControl WithCategoryAxisLabelAngle(int angle) => this with { CategoryAxisLabelAngle = angle };
}
