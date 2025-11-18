using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.Layout.Chart;

/// <summary>
/// Base class for all chart series types.
/// </summary>
[JsonDerivedType(typeof(BarSeries), typeDiscriminator: "bar")]
[JsonDerivedType(typeof(ColumnSeries), typeDiscriminator: "column")]
[JsonDerivedType(typeof(LineSeries), typeDiscriminator: "line")]
[JsonDerivedType(typeof(PieSeries), typeDiscriminator: "pie")]
[JsonDerivedType(typeof(DoughnutSeries), typeDiscriminator: "doughnut")]
[JsonDerivedType(typeof(RadarSeries), typeDiscriminator: "radar")]
[JsonDerivedType(typeof(PolarSeries), typeDiscriminator: "polar")]
[JsonDerivedType(typeof(ScatterSeries), typeDiscriminator: "scatter")]
[JsonDerivedType(typeof(BubbleSeries), typeDiscriminator: "bubble")]
public abstract record ChartSeries
{
    /// <summary>
    /// Gets the data for this series.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Gets the label for this series (shown in legend and tooltips).
    /// </summary>
    public object? Label { get; init; }

    /// <summary>
    /// Gets the background colors for the series.
    /// </summary>
    public object? BackgroundColor { get; init; }

    /// <summary>
    /// Gets the border colors for the series.
    /// </summary>
    public object? BorderColor { get; init; }

    /// <summary>
    /// Gets the border width.
    /// </summary>
    public object? BorderWidth { get; init; }

    /// <summary>
    /// Gets whether this series is hidden.
    /// </summary>
    public object? Hidden { get; init; }

    /// <summary>
    /// Sets the label for this series.
    /// </summary>
    public ChartSeries WithLabel(object label) => this with { Label = label };

    /// <summary>
    /// Sets the background color(s) for this series.
    /// </summary>
    public ChartSeries WithBackgroundColor(object color) => this with { BackgroundColor = color };

    /// <summary>
    /// Sets the border color(s) for this series.
    /// </summary>
    public ChartSeries WithBorderColor(object color) => this with { BorderColor = color };

    /// <summary>
    /// Sets the border width.
    /// </summary>
    public ChartSeries WithBorderWidth(object width) => this with { BorderWidth = width };

    /// <summary>
    /// Hides this series initially.
    /// </summary>
    public ChartSeries WithHidden(object hidden) => this with { Hidden = hidden };
}

/// <summary>
/// Represents a bar chart series.
/// </summary>
public record BarSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public BarSeries() { }

    /// <summary>
    /// Creates a bar series with the specified data.
    /// </summary>
    public BarSeries(IEnumerable<object> data, object? label = null)
    {
        Data = data.ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a bar series with the specified data.
    /// </summary>
    public BarSeries(IEnumerable<double> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Creates a bar series with the specified data.
    /// </summary>
    public BarSeries(IEnumerable<int> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Gets the bar percentage (0-1) for relative bar thickness.
    /// </summary>
    public object? BarPercentage { get; init; }

    /// <summary>
    /// Gets the category percentage (0-1) for spacing.
    /// </summary>
    public object? CategoryPercentage { get; init; }

    /// <summary>
    /// Sets the bar percentage.
    /// </summary>
    public BarSeries WithBarPercentage(object percentage) =>
        this with { BarPercentage = percentage };

    /// <summary>
    /// Sets the category percentage.
    /// </summary>
    public BarSeries WithCategoryPercentage(object percentage) =>
        this with { CategoryPercentage = percentage };
}

/// <summary>
/// Represents a column chart series (vertical bars).
/// </summary>
public record ColumnSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public ColumnSeries() { }

    /// <summary>
    /// Creates a column series with the specified data.
    /// </summary>
    public ColumnSeries(IEnumerable<object> data, object? label = null)
    {
        Data = data.ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a column series with the specified data.
    /// </summary>
    public ColumnSeries(IEnumerable<double> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Creates a column series with the specified data.
    /// </summary>
    public ColumnSeries(IEnumerable<int> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Gets the bar percentage (0-1) for relative bar thickness.
    /// </summary>
    public object? BarPercentage { get; init; }

    /// <summary>
    /// Gets the category percentage (0-1) for spacing.
    /// </summary>
    public object? CategoryPercentage { get; init; }

    /// <summary>
    /// Sets the bar percentage.
    /// </summary>
    public ColumnSeries WithBarPercentage(object percentage) =>
        this with { BarPercentage = percentage };

    /// <summary>
    /// Sets the category percentage.
    /// </summary>
    public ColumnSeries WithCategoryPercentage(object percentage) =>
        this with { CategoryPercentage = percentage };
}

/// <summary>
/// Represents a line chart series.
/// </summary>
public record LineSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public LineSeries() { }

    /// <summary>
    /// Creates a line series with the specified data.
    /// </summary>
    public LineSeries(IEnumerable<object> data, object? label = null)
    {
        Data = data.ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a line series with the specified data.
    /// </summary>
    public LineSeries(IEnumerable<double> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Creates a line series with the specified data.
    /// </summary>
    public LineSeries(IEnumerable<int> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Gets the line tension (0-1) for curved lines.
    /// </summary>
    public object? Tension { get; init; }

    /// <summary>
    /// Gets whether to fill the area under the line.
    /// </summary>
    public object? Fill { get; init; }

    /// <summary>
    /// Gets the point radius.
    /// </summary>
    public object? PointRadius { get; init; }

    /// <summary>
    /// Sets the line tension for curved lines.
    /// </summary>
    public LineSeries WithTension(object tension) => this with { Tension = tension };

    /// <summary>
    /// Enables area fill under the line.
    /// </summary>
    public LineSeries WithFill(object? fill = null) => this with { Fill = fill ?? true };

    /// <summary>
    /// Sets the point radius.
    /// </summary>
    public LineSeries WithPointRadius(object radius) => this with { PointRadius = radius };
}

/// <summary>
/// Represents a pie chart series.
/// </summary>
public record PieSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public PieSeries() { }

    /// <summary>
    /// Creates a pie series with the specified data.
    /// </summary>
    public PieSeries(IEnumerable<object> data, object? label = null)
    {
        Data = data.ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a pie series with the specified data.
    /// </summary>
    public PieSeries(IEnumerable<double> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Creates a pie series with the specified data.
    /// </summary>
    public PieSeries(IEnumerable<int> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }
}

/// <summary>
/// Represents a doughnut chart series.
/// </summary>
public record DoughnutSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public DoughnutSeries() { }

    /// <summary>
    /// Creates a doughnut series with the specified data.
    /// </summary>
    public DoughnutSeries(IEnumerable<object> data, object? label = null)
    {
        Data = data.ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a doughnut series with the specified data.
    /// </summary>
    public DoughnutSeries(IEnumerable<double> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Creates a doughnut series with the specified data.
    /// </summary>
    public DoughnutSeries(IEnumerable<int> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Gets the cutout percentage (0-100) for the center hole size.
    /// </summary>
    public object? Cutout { get; init; }

    /// <summary>
    /// Sets the cutout percentage.
    /// </summary>
    public DoughnutSeries WithCutout(object cutout) => this with { Cutout = cutout };
}

/// <summary>
/// Represents a radar chart series.
/// </summary>
public record RadarSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public RadarSeries() { }

    /// <summary>
    /// Creates a radar series with the specified data.
    /// </summary>
    public RadarSeries(IEnumerable<object> data, object? label = null)
    {
        Data = data.ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a radar series with the specified data.
    /// </summary>
    public RadarSeries(IEnumerable<double> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Creates a radar series with the specified data.
    /// </summary>
    public RadarSeries(IEnumerable<int> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Gets whether to fill the area.
    /// </summary>
    public object? Fill { get; init; }

    /// <summary>
    /// Enables area fill.
    /// </summary>
    public RadarSeries WithFill(object? fill = null) => this with { Fill = fill ?? true };
}

/// <summary>
/// Represents a polar area chart series.
/// </summary>
public record PolarSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public PolarSeries() { }

    /// <summary>
    /// Creates a polar series with the specified data.
    /// </summary>
    public PolarSeries(IEnumerable<object> data, object? label = null)
    {
        Data = data.ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a polar series with the specified data.
    /// </summary>
    public PolarSeries(IEnumerable<double> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }

    /// <summary>
    /// Creates a polar series with the specified data.
    /// </summary>
    public PolarSeries(IEnumerable<int> data, object? label = null)
        : this(data.Cast<object>(), label)
    {
    }
}

/// <summary>
/// Represents a scatter chart series.
/// </summary>
public record ScatterSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public ScatterSeries() { }

    /// <summary>
    /// Creates a scatter series with the specified points.
    /// </summary>
    public ScatterSeries(IEnumerable<(double x, double y)> points, object? label = null)
    {
        Data = points.Select(p => (object)new { x = p.x, y = p.y }).ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a scatter series from separate x and y arrays.
    /// </summary>
    public ScatterSeries(IEnumerable<double> x, IEnumerable<double> y, object? label = null)
    {
        Data = x.Zip(y, (xVal, yVal) => (object)new { x = xVal, y = yVal }).ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Gets the point radius.
    /// </summary>
    public object? PointRadius { get; init; }

    /// <summary>
    /// Sets the point radius.
    /// </summary>
    public ScatterSeries WithPointRadius(object radius) => this with { PointRadius = radius };
}

/// <summary>
/// Represents a bubble chart series.
/// </summary>
public record BubbleSeries : ChartSeries
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public BubbleSeries() { }

    /// <summary>
    /// Creates a bubble series with the specified data points.
    /// </summary>
    public BubbleSeries(IEnumerable<(double x, double y, double r)> points, object? label = null)
    {
        Data = points.Select(p => (object)new { x = p.x, y = p.y, r = p.r }).ToImmutableList();
        Label = label;
    }

    /// <summary>
    /// Creates a bubble series from separate x, y, and radius arrays.
    /// </summary>
    public BubbleSeries(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius, object? label = null)
    {
        var xArray = x.ToArray();
        var yArray = y.ToArray();
        var rArray = radius.ToArray();
        Data = xArray.Select((xVal, i) => (object)new { x = xVal, y = yArray[i], r = rArray[i] }).ToImmutableList();
        Label = label;
    }
}
