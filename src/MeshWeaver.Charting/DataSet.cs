using System.Collections;
using System.Text.Json.Serialization;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Helpers;
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
    /// <returns>A new instance of <see cref="LineDataSet"/>.</returns>
    public static LineDataSet Line(IEnumerable data) => new(data.Cast<object>().ToArray());

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
    /// <returns>A new instance of <see cref="BubbleDataSet"/>.</returns>
    public static BubbleDataSet Bubble(IEnumerable<(double x, double y, double radius)> values) => new(values);

    /// <summary>
    /// Creates a new instance of <see cref="BubbleDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <param name="radius">The radius values.</param>
    /// <returns>A new instance of <see cref="BubbleDataSet"/>.</returns>
    public static BubbleDataSet Bubble(IEnumerable<double> x, IEnumerable<double> y, IEnumerable<double> radius) => new(x, y, radius);

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable data) => new(data.Cast<object>().ToArray());

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable<double> x, IEnumerable<int> y) => new(x, y);

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable<int> x, IEnumerable<double> y) => new(x, y);

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable<int> x, IEnumerable<int> y) => new(x, y);

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/> from separate collections.
    /// </summary>
    /// <param name="x">The x values.</param>
    /// <param name="y">The y values.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable<double> x, IEnumerable<double> y) => new(x, y);

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/> from tuples.
    /// </summary>
    /// <param name="points">The tuples representing the data points.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable<(int x, int y)> points) => new(points);

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/> from tuples.
    /// </summary>
    /// <param name="points">The tuples representing the data points.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable<(int x, double y)> points) => new(points);

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/> from tuples.
    /// </summary>
    /// <param name="points">The tuples representing the data points.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable<(double x, int y)> points) => new(points);

    /// <summary>
    /// Creates a new instance of <see cref="LineScatterDataSet"/> from tuples.
    /// </summary>
    /// <param name="points">The tuples representing the data points.</param>
    /// <returns>A new instance of <see cref="LineScatterDataSet"/>.</returns>
    public static LineScatterDataSet Scatter(IEnumerable<(double x, double y)> points) => new(points);

    /// <summary>
    /// Creates a new instance of <see cref="TimeLineDataSet"/>.
    /// </summary>
    /// <param name="data">The data for the dataset.</param>
    /// <returns>A new instance of <see cref="TimeLineDataSet"/>.</returns>
    public static TimeLineDataSet TimeLine(IEnumerable data) => new(data.Cast<object>().ToArray());

    #endregion    #endregion
}
