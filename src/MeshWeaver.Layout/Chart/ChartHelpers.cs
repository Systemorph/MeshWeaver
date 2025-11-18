namespace MeshWeaver.Layout.Chart;

/// <summary>
/// Helper class for creating charts with fluent API.
/// </summary>
public static class Charts
{
    /// <summary>
    /// Creates a new empty chart.
    /// </summary>
    public static ChartControl Create() => new ChartControl();

    /// <summary>
    /// Creates a bar chart with data and labels.
    /// </summary>
    public static ChartControl Bar(IEnumerable<double> data, IEnumerable<string> labels, string? seriesLabel = null) =>
        new ChartControl()
            .WithSeries(new BarSeries(data, seriesLabel))
            .WithLabels(labels);

    /// <summary>
    /// Creates a bar chart with data and labels.
    /// </summary>
    public static ChartControl Bar(IEnumerable<int> data, IEnumerable<string> labels, string? seriesLabel = null) =>
        new ChartControl()
            .WithSeries(new BarSeries(data, seriesLabel))
            .WithLabels(labels);

    /// <summary>
    /// Creates a bar chart with a single series (without labels).
    /// </summary>
    public static ChartControl Bar(IEnumerable<double> data, string? label = null) =>
        new ChartControl().WithSeries(new BarSeries(data, label));

    /// <summary>
    /// Creates a bar chart with a single series (without labels).
    /// </summary>
    public static ChartControl Bar(IEnumerable<int> data, string? label = null) =>
        new ChartControl().WithSeries(new BarSeries(data, label));

    /// <summary>
    /// Creates a bar chart with multiple series.
    /// </summary>
    public static ChartControl Bar(params BarSeries[] series) =>
        new ChartControl().WithSeries(series);

    /// <summary>
    /// Creates a column chart (vertical bars) with data and labels.
    /// </summary>
    public static ChartControl Column(IEnumerable<double> data, IEnumerable<string> labels, string? seriesLabel = null) =>
        new ChartControl()
            .WithSeries(new ColumnSeries(data, seriesLabel))
            .WithLabels(labels);

    /// <summary>
    /// Creates a column chart (vertical bars) with data and labels.
    /// </summary>
    public static ChartControl Column(IEnumerable<int> data, IEnumerable<string> labels, string? seriesLabel = null) =>
        new ChartControl()
            .WithSeries(new ColumnSeries(data, seriesLabel))
            .WithLabels(labels);

    /// <summary>
    /// Creates a column chart (vertical bars) with a single series (without labels).
    /// </summary>
    public static ChartControl Column(IEnumerable<double> data, string? label = null) =>
        new ChartControl().WithSeries(new ColumnSeries(data, label));

    /// <summary>
    /// Creates a column chart (vertical bars) with a single series (without labels).
    /// </summary>
    public static ChartControl Column(IEnumerable<int> data, string? label = null) =>
        new ChartControl().WithSeries(new ColumnSeries(data, label));

    /// <summary>
    /// Creates a column chart (vertical bars) with multiple series.
    /// </summary>
    public static ChartControl Column(params ColumnSeries[] series) =>
        new ChartControl().WithSeries(series);

    /// <summary>
    /// Creates a line chart with data and labels.
    /// </summary>
    public static ChartControl Line(IEnumerable<double> data, IEnumerable<string> labels, string? seriesLabel = null) =>
        new ChartControl()
            .WithSeries(new LineSeries(data, seriesLabel))
            .WithLabels(labels);

    /// <summary>
    /// Creates a line chart with data and labels.
    /// </summary>
    public static ChartControl Line(IEnumerable<int> data, IEnumerable<string> labels, string? seriesLabel = null) =>
        new ChartControl()
            .WithSeries(new LineSeries(data, seriesLabel))
            .WithLabels(labels);

    /// <summary>
    /// Creates a line chart with a single series (without labels).
    /// </summary>
    public static ChartControl Line(IEnumerable<double> data, string? label = null) =>
        new ChartControl().WithSeries(new LineSeries(data, label));

    /// <summary>
    /// Creates a line chart with a single series (without labels).
    /// </summary>
    public static ChartControl Line(IEnumerable<int> data, string? label = null) =>
        new ChartControl().WithSeries(new LineSeries(data, label));

    /// <summary>
    /// Creates a line chart with multiple series.
    /// </summary>
    public static ChartControl Line(params LineSeries[] series) =>
        new ChartControl().WithSeries(series);

    /// <summary>
    /// Creates a pie chart with data and labels.
    /// </summary>
    public static ChartControl Pie(IEnumerable<double> data, IEnumerable<string> labels) =>
        new ChartControl()
            .WithSeries(new PieSeries(data))
            .WithLabels(labels);

    /// <summary>
    /// Creates a pie chart with data and labels.
    /// </summary>
    public static ChartControl Pie(IEnumerable<int> data, IEnumerable<string> labels) =>
        new ChartControl()
            .WithSeries(new PieSeries(data))
            .WithLabels(labels);

    /// <summary>
    /// Creates a pie chart (without labels).
    /// </summary>
    public static ChartControl Pie(IEnumerable<double> data, string? label = null) =>
        new ChartControl().WithSeries(new PieSeries(data, label));

    /// <summary>
    /// Creates a doughnut chart with data and labels.
    /// </summary>
    public static ChartControl Doughnut(IEnumerable<double> data, IEnumerable<string> labels) =>
        new ChartControl()
            .WithSeries(new DoughnutSeries(data))
            .WithLabels(labels);

    /// <summary>
    /// Creates a doughnut chart with data and labels.
    /// </summary>
    public static ChartControl Doughnut(IEnumerable<int> data, IEnumerable<string> labels) =>
        new ChartControl()
            .WithSeries(new DoughnutSeries(data))
            .WithLabels(labels);

    /// <summary>
    /// Creates a doughnut chart (without labels).
    /// </summary>
    public static ChartControl Doughnut(IEnumerable<double> data, string? label = null) =>
        new ChartControl().WithSeries(new DoughnutSeries(data, label));

    /// <summary>
    /// Creates a radar chart with data and labels.
    /// </summary>
    public static ChartControl Radar(IEnumerable<double> data, IEnumerable<string> labels, string? seriesLabel = null) =>
        new ChartControl()
            .WithSeries(new RadarSeries(data, seriesLabel))
            .WithLabels(labels);

    /// <summary>
    /// Creates a radar chart with a single series (without labels).
    /// </summary>
    public static ChartControl Radar(IEnumerable<double> data, string? label = null) =>
        new ChartControl().WithSeries(new RadarSeries(data, label));

    /// <summary>
    /// Creates a radar chart with multiple series.
    /// </summary>
    public static ChartControl Radar(params RadarSeries[] series) =>
        new ChartControl().WithSeries(series);

    /// <summary>
    /// Creates a polar area chart with data and labels.
    /// </summary>
    public static ChartControl Polar(IEnumerable<double> data, IEnumerable<string> labels) =>
        new ChartControl()
            .WithSeries(new PolarSeries(data))
            .WithLabels(labels);

    /// <summary>
    /// Creates a polar area chart (without labels).
    /// </summary>
    public static ChartControl Polar(IEnumerable<double> data, string? label = null) =>
        new ChartControl().WithSeries(new PolarSeries(data, label));

    /// <summary>
    /// Creates a scatter chart.
    /// </summary>
    public static ChartControl Scatter(IEnumerable<(double x, double y)> points, string? label = null) =>
        new ChartControl().WithSeries(new ScatterSeries(points, label));

    /// <summary>
    /// Creates a scatter chart from separate x and y arrays.
    /// </summary>
    public static ChartControl Scatter(IEnumerable<double> x, IEnumerable<double> y, string? label = null) =>
        new ChartControl().WithSeries(new ScatterSeries(x, y, label));

    /// <summary>
    /// Creates a bubble chart.
    /// </summary>
    public static ChartControl Bubble(IEnumerable<(double x, double y, double r)> points, string? label = null) =>
        new ChartControl().WithSeries(new BubbleSeries(points, label));

    /// <summary>
    /// Creates a bubble chart from separate arrays.
    /// </summary>
    public static ChartControl Bubble(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius, string? label = null) =>
        new ChartControl().WithSeries(new BubbleSeries(x, y, radius, label));

    /// <summary>
    /// Creates a mixed chart with multiple series of different types.
    /// </summary>
    public static ChartControl Mixed(params ChartSeries[] series) =>
        new ChartControl().WithSeries(series);
}

/// <summary>
/// Common chart color palettes.
/// </summary>
public static class ChartPalettes
{
    public static readonly string[] Default = new[]
    {
        "#FF6384", "#36A2EB", "#FFCE56", "#4BC0C0", "#9966FF", "#FF9F40", "#FF6384", "#C9CBCF"
    };

    public static readonly string[] Pastel = new[]
    {
        "#FFB6C1", "#87CEEB", "#FFE4B5", "#98D8C8", "#DDA0DD", "#F0E68C"
    };

    public static readonly string[] Bold = new[]
    {
        "#DC143C", "#0000FF", "#FFD700", "#008000", "#8B008B", "#FF4500"
    };

    public static readonly string[] Earth = new[]
    {
        "#8B4513", "#228B22", "#DAA520", "#2F4F4F", "#8B7355", "#556B2F"
    };
}
