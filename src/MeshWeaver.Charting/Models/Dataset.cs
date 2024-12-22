using System.Text.Json.Serialization;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Helpers;
using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Models;

public abstract record DataSet(IReadOnlyCollection<object> Data)
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
    public string Label { get; init; }

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

    public ChartType? Type { get; init; }
    internal virtual bool HasLabel() => Label != null;



}

// https://www.chartjs.org/docs/3.5.1/general/data-structures.html
public abstract record DataSet<TDataSet>(IReadOnlyCollection<object> Data) : DataSet(Data) where TDataSet : DataSet<TDataSet>
{
    public TDataSet This => (TDataSet)this;

    /// <summary>
    /// Sets the label for the dataset which appears in the legend and tooltips.
    /// </summary>
    /// <param name="label">The label for the dataset.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified label.</returns>
    public TDataSet WithLabel(string label) =>
        This with { Label = label };

    /// <summary>
    /// Sets the data labels for the dataset.
    /// </summary>
    /// <param name="dataLabels">The data labels for the dataset.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified data labels.</returns>
    public TDataSet WithDataLabels(DataLabels dataLabels) =>
        This with { DataLabels = dataLabels };

    /// <summary>
    /// Sets the line width for the dataset.
    /// </summary>
    /// <param name="width">The line width in pixels.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified line width.</returns>
    public TDataSet WithLineWidth(int width) => WithBorderWidth(width);

    /// <summary>
    /// Sets the border width for the dataset.
    /// </summary>
    /// <param name="width">The border width in pixels.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified border width.</returns>
    public TDataSet WithBorderWidth(int width) =>
        This with { BorderWidth = width };

    /// <summary>
    /// Sets the border width for the dataset.
    /// </summary>
    /// <param name="widths">The border widths in pixels.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified border widths.</returns>
    public TDataSet WithBorderWidth(IEnumerable<int> widths) =>
        This with { BorderWidth = widths };

    /// <summary>
    /// Sets the type of the chart.
    /// </summary>
    /// <param name="type">The type of the chart.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified chart type.</returns>
    public TDataSet SetType(ChartType? type) =>
        This with { Type = type };

    /// <summary>
    /// Sets the type of the chart.
    /// </summary>
    /// <param name="type">The type of the chart as a string.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified chart type.</returns>
    public TDataSet SetType(string type) =>
        This with
        {
            Type = type switch
            {
                "bar" => ChartType.Bar,
                "bubble" => ChartType.Bubble,
                "radar" => ChartType.Radar,
                "polarArea" => ChartType.PolarArea,
                "pie" => ChartType.Pie,
                "line" => ChartType.Line,
                "doughnut" => ChartType.Doughnut,
                "scatter" => ChartType.Scatter,
                _ => throw new ArgumentException(nameof(type))
            }
        };

    /// <summary>
    /// Sets the background color for the dataset.
    /// </summary>
    /// <param name="colors">The background colors.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified background colors.</returns>
    public TDataSet WithBackgroundColor(IEnumerable<ChartColor> colors) =>
        This with { BackgroundColor = colors };

    /// <summary>
    /// Sets the background color for the dataset.
    /// </summary>
    /// <param name="color">The background color.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified background color.</returns>
    public TDataSet WithBackgroundColor(ChartColor color) =>
        This with { BackgroundColor = color };

    /// <summary>
    /// Sets the hover background color for the dataset.
    /// </summary>
    /// <param name="colors">The hover background colors.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified hover background colors.</returns>
    public TDataSet WithHoverBackgroundColor(IEnumerable<ChartColor> colors) =>
        This with { HoverBackgroundColor = colors };

    /// <summary>
    /// Sets the hover background color for the dataset.
    /// </summary>
    /// <param name="color">The hover background color.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified hover background color.</returns>
    public TDataSet WithHoverBackgroundColor(ChartColor color) =>
        This with { HoverBackgroundColor = color };

    /// <summary>
    /// Sets the parsing options for the dataset.
    /// </summary>
    /// <param name="parsing">The parsing options.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified parsing options.</returns>
    public TDataSet WithParsing(Parsing parsing) =>
        This with { Parsing = parsing };

}

public record Parsing(string XAxisKey, string YAxisKey);

internal record TimePointData
{
    public string X { get; init; }
    public double? Y { get; init; }
}

internal record PointData
{
    public double? X { get; init; }
    public double? Y { get; init; }
}

internal record BubbleData
{
    public double X { get; init; }
    public double Y { get; init; }
    public double R { get; init; }
}
