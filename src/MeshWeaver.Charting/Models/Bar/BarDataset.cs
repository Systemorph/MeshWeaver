using System.Collections;
using System.Diagnostics.CodeAnalysis;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Utils;

namespace MeshWeaver.Charting.Models.Bar;

/// <summary>
/// https://www.chartjs.org/docs/latest/charts/bar.html
/// </summary>
[SuppressMessage("ReSharper", "InconsistentNaming")]
public record BarDataSet(IReadOnlyCollection<object> Data, string Label = null) : DataSetBase<BarDataSet>(Data, Label), IDataSetWithOrder<BarDataSet>, IDataSetWithPointStyle<BarDataSet>, IDataSetWithStack<BarDataSet>
{
    public BarDataSet(IEnumerable Data, string label = null) : this(Data.Cast<object>().ToArray())
    {
        Label = label;
    }

    #region General

    /// <summary>
    /// Base value for the bar in data units along the value axis. If not set, defaults to the value axis base value.
    /// </summary>
    //[JsonProperty("base")]
    public int? Base { get; init; }

    /// <summary>
    /// Should the bars be grouped on index axis. When true, all the datasets at same index value will be placed next to each other centering on that index value. When false, each bar is placed on its actual index-axis value.
    /// </summary>
    public bool? Grouped { get; init; }

    /// <summary>
    /// The base axis of the dataset. 'x' for vertical bars and 'y' for horizontal bars.
    /// </summary>
    public string IndexAxis { get; init; }

    /// <summary>
    /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
    /// </summary>
    public int? Order { get; init; }

    /// <summary>
    /// If true, null or undefined values will not be used for spacing calculations when determining bar size.
    /// </summary>
    public bool? SkipNull { get; init; }

    /// <summary>
    /// The ID of the group to which this dataset belongs to (when stacked, each group will be a separate stack). Defaults to dataset type.
    /// </summary>
    public string Stack { get; init; }

    public BarDataSet WithStack(string stack) => this with { Stack = stack };

    /// <summary>
    /// The ID of the x axis to plot this dataset on.
    /// </summary>
    public string XAxisID { get; init; }

    /// <summary>
    /// The ID of the y axis to plot this dataset on.
    /// </summary>
    public string YAxisID { get; init; }
    #endregion

    #region Styling
    // https://www.chartjs.org/docs/latest/charts/bar.html#styling
    /// <summary>
    /// Which edge to skip drawing the border for. Options are 'bottom', 'left', 'top', and 'right'.
    /// </summary>
    public IEnumerable<string> BorderSkipped { get; init; }

    /// <summary>
    /// The bar border radius (in pixels).
    /// </summary>
    public object BorderRadius { get; init; }

    /// <summary>
    /// Set this to ensure that bars have a minimum length in pixels.
    /// </summary>
    public double? MinBarLength { get; init; }

    /// <summary>
    /// Style of the point for legend.
    /// </summary>
    public Shapes? PointStyle { get; init; }
    #endregion Styling

    #region Interactions
    // https://www.chartjs.org/docs/latest/charts/bar.html#interactions
    /// <summary>
    /// The bar border width when hovered (in pixels).
    /// </summary>
    public int? HoverBorderWidth { get; init; }

    /// <summary>
    /// The bar border radius when hovered (in pixels).
    /// </summary>
    public int? HoverBorderRadius { get; init; }
    #endregion Interactions

    #region BarPercentage
    // https://www.chartjs.org/docs/latest/charts/bar.html#barpercentage
    /// <summary>
    /// Percent (0-1) of the available width each bar should be within the category percentage. 1.0 will take the whole category width and put the bars right next to each other.
    /// </summary>
    public double? BarPercentage { get; init; }
    #endregion BarPercentage

    #region CategoryPercentage
    // https://www.chartjs.org/docs/latest/charts/bar.html#categorypercentage
    /// <summary>
    /// Percent (0-1) of the available width each category should be within the sample width.
    /// </summary>
    public double? CategoryPercentage { get; init; }
    #endregion CategoryPercentage

    #region BarThickness
    // https://www.chartjs.org/docs/latest/charts/bar.html#barthickness
    /// <summary>
    /// Manually set width of each bar in pixels. If set to 'flex', it computes "optimal" sample widths that globally arrange bars side by side. If not set (default), bars are equally sized based on the smallest interval.
    /// </summary>
    public object BarThickness { get; init; }
    #endregion BarThickness

    #region MaxBarThickness
    // https://www.chartjs.org/docs/latest/charts/bar.html#maxbarthickness
    /// <summary>
    /// Set this to ensure that bars are not sized thicker than this.
    /// </summary>
    public double? MaxBarThickness { get; init; }
    #endregion MaxBarThickness

    public Scale Scale { get; init; }

    /// <summary>
    /// Sets the bar percentage for the dataset.
    /// </summary>
    /// <param name="percentage">The bar percentage (0-1).</param>
    /// <returns>A new instance of <see cref="BarDataSet"/> with the specified bar percentage.</returns>
    public BarDataSet WithBarPercentage(double percentage) =>
        this with { BarPercentage = percentage };

    /// <summary>
    /// Sets the category percentage for the dataset.
    /// </summary>
    /// <param name="percentage">The category percentage (0-1).</param>
    /// <returns>A new instance of <see cref="BarDataSet"/> with the specified category percentage.</returns>
    public BarDataSet WithCategoryPercentage(double percentage) =>
        this with { CategoryPercentage = percentage };

    /// <summary>
    /// Sets the x-axis ID for the dataset.
    /// </summary>
    /// <param name="xAxisId">The x-axis ID.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/> with the specified x-axis ID.</returns>
    public BarDataSet WithXAxis(string xAxisId) =>
        this with { XAxisID = xAxisId };

    /// <summary>
    /// Sets the y-axis ID for the dataset.
    /// </summary>
    /// <param name="yAxisId">The y-axis ID.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/> with the specified y-axis ID.</returns>
    public BarDataSet WithYAxis(string yAxisId) =>
        this with { YAxisID = yAxisId };

    /// <summary>
    /// Sets the stack for the dataset.
    /// </summary>
    /// <param name="stack">The stack.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/> with the specified stack.</returns>
    public BarDataSet WithStack(object stack) =>
        this with { Stack = stack.ToString() };

    /// <summary>
    /// Sets whether the bars should be grouped.
    /// </summary>
    /// <param name="grouped">Whether the bars should be grouped.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/> with the specified grouping.</returns>
    public BarDataSet WithGrouped(bool grouped) =>
        this with { Grouped = grouped };

    /// <summary>
    /// Sets the drawing order of the dataset.
    /// </summary>
    /// <param name="order">The drawing order of the dataset.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/> with the specified order.</returns>
    public BarDataSet WithOrder(int? order) =>
        this with { Order = order };

    /// <summary>
    /// Sets the style of the point for the legend.
    /// </summary>
    /// <param name="pointStyle">The style of the point for the legend.</param>
    /// <returns>A new instance of <see cref="BarDataSet"/> with the specified point style.</returns>
    public BarDataSet WithPointStyle(Shapes? pointStyle) =>
        this with { PointStyle = pointStyle };

    public BarDataSet WithBarThickness(object value)
        => this with { BarThickness = value };

    public override ChartType? Type => ChartType.Bar;
}


public record FloatingBarDataSet : BarDataSet
{
    private static readonly Parsing FloatingParsing =
        new Parsing($"{nameof(WaterfallBar.Label).ToCamelCase()}",
            $"{nameof(WaterfallBar.Range).ToCamelCase()}");

    public FloatingBarDataSet(IEnumerable Data, string label = null) : base(Data, label)
    {
    }

    public FloatingBarDataSet(IReadOnlyCollection<object> Data) : base(Data)
    {
    }

    public FloatingBarDataSet(IEnumerable dataFrom, IEnumerable dataTo, string label = null) : this(ConvertToFloatingData(dataFrom, dataTo), label)
    {
        
    }

    private static IReadOnlyCollection<object> ConvertToFloatingData(IEnumerable dataFrom, IEnumerable dataTo)
    {
        var dataFromArray = dataFrom?.Cast<object>().ToArray();
        var dataToArray = dataTo?.Cast<object>().ToArray();
        if (dataFromArray == null || dataToArray == null) return null;

        var rangeData = dataFromArray.Zip(dataToArray, (from, to) => new[] { from, to });
        return rangeData.Cast<object>().ToArray();
    }
}

public record HorizontalFloatingBarDataSet : BarDataSet, IChartOptionsConfiguration
{
    public HorizontalFloatingBarDataSet(IEnumerable Data, string label = null) : base(Data, label)
    {
    }

    public HorizontalFloatingBarDataSet(IReadOnlyCollection<object> Data) : base(Data)
    {
    }

    public ChartOptions Configure(ChartOptions options)
    {
        return options.WithIndexAxis("y");
    }
}
