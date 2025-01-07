using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Radar;

/// <summary>
/// Represents a dataset for a radar chart.
/// </summary>
public record RadarDataSet(IReadOnlyCollection<object> Data, string Label = null) : DataSetBase<RadarDataSet>(Data, Label),
    IDataSetWithOrder<RadarDataSet>, IDataSetWithPointStyle<RadarDataSet>, IDataSetWithFill<RadarDataSet>, IDataSetWithTension<RadarDataSet>, IDataSetWithPointRadiusAndRotation<RadarDataSet>
{
    #region General

    // https://www.chartjs.org/docs/latest/charts/radar.html#general
    /// <summary>
    /// Draw the active points of a dataset over the other points of the dataset.
    /// </summary>
    public bool? DrawActiveElementsOnTop { get; init; }

    /// <summary>
    /// Sets whether to draw the active points of a dataset over the other points of the dataset.
    /// </summary>
    /// <param name="drawActiveElementsOnTop">Whether to draw the active points of a dataset over the other points of the dataset.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified value.</returns>
    public RadarDataSet WithDrawActiveElementsOnTop(bool? drawActiveElementsOnTop) =>
        this with { DrawActiveElementsOnTop = drawActiveElementsOnTop };

    /// <summary>
    /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
    /// </summary>
    public int? Order { get; init; }

    #endregion General

    #region Styling

    // https://www.chartjs.org/docs/latest/charts/radar.html#styling
    /// <summary>
    /// Bubble shape style.
    /// </summary>
    public Shapes? PointStyle { get; init; }

    /// <summary>
    /// Bubble rotation (in degrees).
    /// </summary>
    public int? Rotation { get; init; }

    /// <summary>
    /// Sets the bubble rotation (in degrees).
    /// </summary>
    /// <param name="rotation">The bubble rotation in degrees.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified rotation.</returns>
    public RadarDataSet WithRotation(int? rotation) =>
        this with { Rotation = rotation };

    /// <summary>
    /// Bubble radius (in pixels).
    /// </summary>
    public int? Radius { get; init; }

    /// <summary>
    /// Sets the bubble radius (in pixels).
    /// </summary>
    /// <param name="radius">The bubble radius in pixels.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified radius.</returns>
    public RadarDataSet WithRadius(int? radius) =>
        this with { Radius = radius };

    #endregion Styling

    #region Interactions

    // https://www.chartjs.org/docs/latest/charts/radar.html#interactions
    /// <summary>
    /// Bubble additional radius for hit detection (in pixels).
    /// </summary>
    public int? HitRadius { get; init; }

    /// <summary>
    /// Sets the bubble additional radius for hit detection (in pixels).
    /// </summary>
    /// <param name="hitRadius">The bubble additional radius for hit detection in pixels.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified hit radius.</returns>
    public RadarDataSet WithHitRadius(int? hitRadius) =>
        this with { HitRadius = hitRadius };

    /// <summary>
    /// Bubble border width when hovered (in pixels).
    /// </summary>
    public int? HoverBorderWidth { get; init; }

    /// <summary>
    /// Sets the bubble border width when hovered (in pixels).
    /// </summary>
    /// <param name="hoverBorderWidth">The bubble border width when hovered in pixels.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified hover border width.</returns>
    public RadarDataSet WithHoverBorderWidth(int? hoverBorderWidth) =>
        this with { HoverBorderWidth = hoverBorderWidth };

    /// <summary>
    /// Bubble additional radius when hovered (in pixels).
    /// </summary>
    public int? HoverRadius { get; init; }

    /// <summary>
    /// Sets the bubble additional radius when hovered (in pixels).
    /// </summary>
    /// <param name="hoverRadius">The bubble additional radius when hovered in pixels.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified hover radius.</returns>
    public RadarDataSet WithHoverRadius(int? hoverRadius) =>
        this with { HoverRadius = hoverRadius };

    #endregion Interactions

    /// <summary>
    /// Sets the drawing order of the dataset.
    /// </summary>
    /// <param name="order">The drawing order of the dataset.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified order.</returns>
    public RadarDataSet WithOrder(int? order) =>
        this with { Order = order };

    /// <summary>
    /// Sets the style of the point for the legend.
    /// </summary>
    /// <param name="pointStyle">The style of the point for the legend.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified point style.</returns>
    public RadarDataSet WithPointStyle(Shapes? pointStyle) =>
        this with { PointStyle = pointStyle };

    /// <summary>
    /// The fill option for the dataset.
    /// </summary>
    public object Fill { get; init; }

    /// <summary>
    /// Sets the fill option for the dataset.
    /// </summary>
    /// <param name="fill">The fill option.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified fill option.</returns>
    public RadarDataSet WithFill(object fill) =>
        this with { Fill = fill };

    /// <summary>
    /// Sets the fill option to fill the area under the line.
    /// </summary>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the fill option set to fill the area under the line.</returns>
    public RadarDataSet WithArea() =>
        this with { Fill = true };

    /// <summary>
    /// Disables the fill option for the dataset.
    /// </summary>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the fill option disabled.</returns>
    public RadarDataSet WithoutFill() =>
        this with { Fill = false };

    /// <summary>
    /// Bezier curve tension of the line. Set to 0 to draw straight lines. This option is ignored if monotone cubic interpolation is used.
    /// </summary>
    public double? Tension { get; init; }

    /// <summary>
    /// Sets the Bezier curve tension of the line.
    /// </summary>
    /// <param name="tension">The Bezier curve tension of the line.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified tension.</returns>
    public RadarDataSet WithTension(double? tension) =>
        this with { Tension = tension };

    /// <summary>
    /// The radius of the point shape. If set to 0, nothing is rendered.
    /// </summary>
    public int? PointRadius { get; init; }

    /// <summary>
    /// Sets the radius of the point shape.
    /// </summary>
    /// <param name="pointRadius">The radius of the point shape.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified point radius.</returns>
    public RadarDataSet WithPointRadius(int? pointRadius) =>
        this with { PointRadius = pointRadius };

    /// <summary>
    /// The rotation of the point in degrees.
    /// </summary>
    public int? PointRotation { get; init; }

    /// <summary>
    /// Sets the rotation of the point in degrees.
    /// </summary>
    /// <param name="pointRotation">The rotation of the point in degrees.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified point rotation.</returns>
    public RadarDataSet WithPointRotation(int? pointRotation) =>
        this with { PointRotation = pointRotation };

    /// <summary>
    /// Sets the radius and rotation of the point.
    /// </summary>
    /// <param name="pointRadius">The radius of the point shape.</param>
    /// <param name="pointRotation">The rotation of the point in degrees.</param>
    /// <returns>A new instance of <see cref="RadarDataSet"/> with the specified point radius and rotation.</returns>
    public RadarDataSet WithPointRadiusAndRotation(int? pointRadius, int? pointRotation) =>
        this with { PointRadius = pointRadius, PointRotation = pointRotation };

    public override ChartType? Type => ChartType.Radar;
}
