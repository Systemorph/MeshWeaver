using MeshWeaver.Layout.Palettes;
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
/// Chart colour palettes, generated by the platform palette engine (<see cref="CuspPalette"/>)
/// rather than hand-picked: within a categorical palette every colour shares ONE perceptual
/// lightness and takes the chroma its own hue can carry there, so no series shouts louder than
/// another and every colour is inside sRGB. The named palettes stay FIELDS (as they were before the
/// engine) so modules compiled against the old surface keep binding.
/// </summary>
public static class ChartPalettes
{
    /// <summary>Default categorical palette: eight evenly spaced hues at mid lightness, starting at blue.</summary>
    public static readonly string[] Default = CuspPalette.Categorical(8).ToHex();

    /// <summary>Soft pastel palette: the same eight hues, light and gently saturated.</summary>
    public static readonly string[] Pastel = CuspPalette.Categorical(8, lightness: 0.86, saturation: 0.5).ToHex();

    /// <summary>Bold palette: the same eight hues, darker and through the cusp — legible under white labels.</summary>
    public static readonly string[] Bold = CuspPalette.Categorical(8, lightness: 0.55, saturation: 1.0).ToHex();

    /// <summary>Earth-tone palette: six muted hues from clay to moss.</summary>
    public static readonly string[] Earth = CuspPalette.Categorical(6, lightness: 0.52, saturation: 0.45, startHue: 40, hueSpan: 110).ToHex();

    /// <summary>
    /// The categorical palette sized to <paramref name="count"/> series — hues spread evenly over the
    /// whole wheel for exactly that many, rather than the first <paramref name="count"/> of a fixed list.
    /// </summary>
    public static string[] Categorical(int count) => CuspPalette.Categorical(count).ToHex();

    /// <summary>A one-hue ramp, dark to light, for ordered values (heat maps, intensities).</summary>
    public static string[] Sequential(int count, double hue = 255) => CuspPalette.Sequential(count, hue).ToHex();

    /// <summary>A two-hue diverging ramp around a light centre, for values above and below a midpoint.</summary>
    public static string[] Diverging(int count, double hueLow = 25, double hueHigh = 255) =>
        CuspPalette.Diverging(count, hueLow, hueHigh).ToHex();
}
