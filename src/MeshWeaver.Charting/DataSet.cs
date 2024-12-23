using System.Collections;
using System.Text.Json.Serialization;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Bubble;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Polar;
using MeshWeaver.Charting.Models.Radar;
using MeshWeaver.Charting.Models.Segmented;

namespace MeshWeaver.Charting;

public abstract record DataSet(IReadOnlyCollection<object> Data, string Label)
{
    /// <summary>
    /// The fill color under the line.
    /// ChartColor or IEnumerable&lt;ChartColor&gt;
    /// </summary>
    public object BackgroundColor { get; init; }

    /// <summary>
    /// The color of the line.
    /// </summary>
    public IEnumerable<ChartColor> BorderColor { get; init; }

    /// <summary>
    /// The width of the line in pixels.
    /// </summary>
    public object BorderWidth { get; init; }

    /// <summary>
    /// How to clip relative to chartArea. Positive value allows overflow, negative value clips that many pixels inside chartArea. 0 = clip at chartArea. Clipping can also be configured per side: clip: {left: 5, top: false, right: -2, bottom: 0}
    /// </summary>
    public object Clip { get; init; }

    /// <summary>
    /// Point background color when hovered.
    /// ChartColor or IEnumerable&lt;ChartColor&gt;
    /// </summary>
    public object HoverBackgroundColor { get; init; }

    /// <summary>
    /// Point border color when hovered.
    /// </summary>
    public IEnumerable<ChartColor> HoverBorderColor { get; init; }

    /// <summary>
    /// The label for the dataset which appears in the legend and tooltips.
    /// </summary>
    public string Label { get; init; } = Label;

    [JsonPropertyName("datalabels")]
    public DataLabels DataLabels { get; set; }

    /// <summary>
    /// How to parse the dataset. The parsing can be disabled by specifying parsing: false at chart options or dataset. If parsing is disabled, data must be sorted and in the formats the associated chart type and scales use internally.
    /// </summary>
    public Parsing Parsing { get; init; }

    /// <summary>
    /// Start DataSet Disabled if set to True
    /// </summary>
    public bool? Hidden { get; init; }

    internal abstract ChartType ChartType { get; }
    public ChartType? Type { get; init; }

    internal virtual bool HasLabel() => Label != null;


    #region Static Constructors for data sets

    /// <summary>
    /// Creates a new instance of <see cref="BarDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/>.</returns>
    public static BarDataSet Bar(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);
    /// <summary>
    /// Creates a new instance of <see cref="FloatingBarDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/>.</returns>
    public static FloatingBarDataSet FloatingBar(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);


    /// <summary>
    /// Creates a floating bar chart model.
    /// </summary>
    /// <param name="dataFrom">The starting values of the data range.</param>
    /// <param name="dataTo">The ending values of the data range.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a floating bar chart.</returns>
    public static FloatingBarDataSet FloatingBar(IEnumerable dataFrom, IEnumerable dataTo, string label = null)
        => new(dataFrom, dataTo, label);
    /// <summary>
    /// Creates a new instance of <see cref="HorizontalFloatingBarDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/>.</returns>
    public static HorizontalFloatingBarDataSet HorizontalFloatingBar(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);

    /// <summary>
    /// Creates a new instance of <see cref="DoughnutDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/>.</returns>
    public static DoughnutDataSet Doughnut(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);

    /// <summary>
    /// Creates a new instance of <see cref="LineDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="LineDataSet"/>.</returns>
    public static LineDataSet Line(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);

    /// <summary>
    /// Creates a new instance of <see cref="PieDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="PieDataSet"/>.</returns>
    public static PieDataSet Pie(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);

    /// <summary>
    /// Creates a new instance of <see cref="PolarDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="PolarDataSet"/>.</returns>
    public static PolarDataSet Polar(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);

    /// <summary>
    /// Creates a new instance of <see cref="RadarDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/>.</returns>
    public static RadarDataSet Radar(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);

    /// <summary>
    /// Creates a new instance of <see cref="BubbleDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="BubbleDataSet"/>.</returns>
    public static BubbleDataSet Bubble(IEnumerable<BubbleData> data, string label = null) => new(data.ToArray(), label);

    /// <summary>
    /// Creates a new instance of <see cref="BubbleDataSet"/> from tuples.
    /// </summary>
    /// <param name="values">The tuples representing the data points.</param>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <see cref="BubbleDataSet"/>.</returns>
    public static BubbleDataSet Bubble(IEnumerable<(double x, double y, double radius)> values, string label = null) => new(values, label);

    /// <summary>
    /// Creates a new instance of <see cref="BubbleDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="radius">The radius values.</param>
    /// <param name="label"></param>
    /// <returns>A new instance of <see cref="BubbleDataSet"/>.</returns>
    public static BubbleDataSet Bubble(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius, string label = null) => new(x, y, radius, label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <param name="label"></param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable data, string label = null) => new(data.Cast<object>().ToArray(), label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable<double> x, IEnumerable<int> y, string label = null) => new(x, y, label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable<int> x, IEnumerable<double> y, string label = null) => new(x, y, label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable<int> x, IEnumerable<int> y, string label = null) => new(x, y, label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable<double> x, IEnumerable<double> y, string label = null) => new(x, y, label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/> from tuples.
    /// </summary>
    /// <param name="points">The tuples representing the data points.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable<(int x, int y)> points, string label = null) => new(points, label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/> from tuples.
    /// </summary>
    /// <param name="points">The tuples representing the data points.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable<(int x, double y)> points, string label = null) => new(points, label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/> from tuples.
    /// </summary>
    /// <param name="points">The tuples representing the data points.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable<(double x, int y)> points, string label = null) => new(points, label);

    /// <summary>
    /// Creates a new instance of <see cref="ScatterDataSet"/> from tuples.
    /// </summary>
    /// <param name="points">The tuples representing the data points.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ScatterDataSet"/>.</returns>
    public static ScatterDataSet Scatter(IEnumerable<(double x, double y)> points, string label = null) => new(points, label);

    /// <summary>
    /// Creates a timeline chart model.
    /// </summary>
    /// <param name="dates">The dates for the x-axis.</param>
    /// <param name="rawData">The data values for the y-axis.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a timeline chart.</returns>
    public static TimeLineDataSet TimeLine(IEnumerable<DateTime> dates, IEnumerable<double> rawData, string label = null) => new(dates, rawData, label);
    /// <summary>
    /// Creates a timeline chart model.
    /// </summary>
    /// <param name="times">The times for the x-axis.</param>
    /// <param name="rawData">The data values for the y-axis.</param>
    /// <param name="label">The label of the data set.</param>
    /// <returns>A new instance of <see cref="ChartModel"/> representing a timeline chart.</returns>
    public static TimeLineDataSet TimeLine(IEnumerable<string> times, IEnumerable<double> rawData, string label = null) =>
        new(times, rawData, label);


    #endregion    #endregion
}
