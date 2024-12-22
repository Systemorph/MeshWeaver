namespace MeshWeaver.Charting.Models.Segmented;

public record DoughnutDataSet(IReadOnlyCollection<object> Data) : SegmentDataSetBase<DoughnutDataSet>(Data)
{
    #region BorderAlign

    // https://www.chartjs.org/docs/latest/charts/doughnut.html#border-alignment
    /// <summary>
    ///     The following values are supported for borderAlign.
    ///     'center' (default)
    ///     'inner'
    ///     When 'center' is set, the borders of arcs next to each other will overlap.When 'inner' is set, it is guaranteed
    ///     that all borders will not overlap.
    /// </summary>
    public string BorderAlign { get; init; }

    /// <summary>
    /// Sets the border alignment of the arcs.
    /// </summary>
    /// <param name="borderAlign">The border alignment of the arcs.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified border alignment.</returns>
    public DoughnutDataSet WithBorderAlign(string borderAlign) =>
        This with { BorderAlign = borderAlign };

    #endregion BorderAlign

    #region BorderRadius

    // https://www.chartjs.org/docs/latest/charts/doughnut.html#border-radius
    /// <summary>
    ///     If this value is a number, it is applied to all corners of the arc (outerStart, outerEnd, innerStart, innerRight).
    ///     If this value is an object, the outerStart property defines the outer-start corner's border radius. Similarly, the
    ///     outerEnd, innerStart, and innerEnd properties can also be specified.
    /// </summary>
    public object BorderRadius { get; init; }

    /// <summary>
    /// Sets the border radius of the arcs.
    /// </summary>
    /// <param name="borderRadius">The border radius of the arcs.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified border radius.</returns>
    public DoughnutDataSet WithBorderRadius(object borderRadius) =>
        This with { BorderRadius = borderRadius };

    #endregion BorderRadius

    #region Styling

    // https://www.chartjs.org/docs/latest/charts/doughnut.html#styling
    /// <summary>
    ///     Arc border join style.
    /// </summary>
    public IEnumerable<string> BorderJoinStyle { get; init; }

    /// <summary>
    /// Sets the border join style of the arcs.
    /// </summary>
    /// <param name="borderJoinStyle">The border join style of the arcs.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified border join style.</returns>
    public DoughnutDataSet WithBorderJoinStyle(IEnumerable<string> borderJoinStyle) =>
        This with { BorderJoinStyle = borderJoinStyle };

    /// <summary>
    ///     Arc offset (in pixels).
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Sets the offset of the arcs.
    /// </summary>
    /// <param name="offset">The offset of the arcs in pixels.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified offset.</returns>
    public DoughnutDataSet WithOffset(int? offset) =>
        This with { Offset = offset };

    /// <summary>
    ///     Fixed arc offset (in pixels). Similar to offset but applies to all arcs.
    /// </summary>
    public int? Spacing { get; init; }

    /// <summary>
    /// Sets the fixed offset of the arcs.
    /// </summary>
    /// <param name="spacing">The fixed offset of the arcs in pixels.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified spacing.</returns>
    public DoughnutDataSet WithSpacing(int? spacing) =>
        This with { Spacing = spacing };

    /// <summary>
    ///     The relative thickness of the dataset. Providing a value for weight will cause the pie or doughnut dataset to be
    ///     drawn with a thickness relative to the sum of all the dataset weight values.
    /// </summary>
    public int? Weight { get; init; }

    /// <summary>
    /// Sets the relative thickness of the dataset.
    /// </summary>
    /// <param name="weight">The relative thickness of the dataset.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified weight.</returns>
    public DoughnutDataSet WithWeight(int? weight) =>
        This with { Weight = weight };

    #endregion Styling

    #region Interactions

    // https://www.chartjs.org/docs/latest/charts/doughnut.html#interactions
    /// <summary>
    ///     Arc border join style when hovered.
    /// </summary>
    public string HoverBorderJoinStyle { get; init; }

    /// <summary>
    /// Sets the border join style of the arcs when hovered.
    /// </summary>
    /// <param name="hoverBorderJoinStyle">The border join style of the arcs when hovered.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified hover border join style.</returns>
    public DoughnutDataSet WithHoverBorderJoinStyle(string hoverBorderJoinStyle) =>
        This with { HoverBorderJoinStyle = hoverBorderJoinStyle };

    /// <summary>
    ///     Arc border width when hovered (in pixels).
    /// </summary>
    public int? HoverBorderWidth { get; init; }

    /// <summary>
    /// Sets the border width of the arcs when hovered.
    /// </summary>
    /// <param name="hoverBorderWidth">The border width of the arcs when hovered in pixels.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified hover border width.</returns>
    public DoughnutDataSet WithHoverBorderWidth(int? hoverBorderWidth) =>
        This with { HoverBorderWidth = hoverBorderWidth };

    /// <summary>
    ///     Arc offset when hovered (in pixels).
    /// </summary>
    public int? HoverOffset { get; init; }

    /// <summary>
    /// Sets the offset of the arcs when hovered.
    /// </summary>
    /// <param name="hoverOffset">The offset of the arcs when hovered in pixels.</param>
    /// <returns>A new instance of <see cref="DoughnutDataSet"/> with the specified hover offset.</returns>
    public DoughnutDataSet WithHoverOffset(int? hoverOffset) =>
        This with { HoverOffset = hoverOffset };

    #endregion Interactions
}
