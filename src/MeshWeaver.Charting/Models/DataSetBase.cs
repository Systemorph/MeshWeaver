using MeshWeaver.Charting.Helpers;
using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Models;

// https://www.chartjs.org/docs/3.5.1/general/data-structures.html
public abstract record DataSetBase<TDataSet>(IReadOnlyCollection<object> Data, string Label) : DataSet(Data, Label) where TDataSet : DataSetBase<TDataSet>
{
    protected TDataSet This => (TDataSet)this;

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

public record BubbleData(double X, double Y, double R);
