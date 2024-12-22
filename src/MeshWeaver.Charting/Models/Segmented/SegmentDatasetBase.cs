namespace MeshWeaver.Charting.Models.Segmented;

public abstract record SegmentDataSetBase<TDataSet>(IReadOnlyCollection<object> Data) : DataSet<TDataSet>(Data) where TDataSet : SegmentDataSetBase<TDataSet>
{
    /// <summary>
    /// The portion of the chart that is cut out of the middle. If string and ending with '%', percentage of the chart radius. number is considered to be pixels.
    /// </summary>
    public object Cutout { get; init; }

    /// <summary>
    /// The outer radius of the chart. If string and ending with '%', percentage of the maximum radius. number is considered to be pixels.
    /// </summary>
    public object Radius { get; init; }

    /// <summary>
    /// Starting angle to draw arcs from.
    /// </summary>
    public int? Rotation { get; init; }

    /// <summary>
    /// Sweep to allow arcs to cover.
    /// </summary>
    public int? Circumference { get; init; }

    /// <summary>
    /// Sets the portion of the chart that is cut out of the middle.
    /// </summary>
    /// <param name="cutout">If string and ending with '%', percentage of the chart radius. number is considered to be pixels.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified cutout.</returns>
    public TDataSet WithCutout(object cutout) =>
        This with { Cutout = cutout };

    /// <summary>
    /// Sets the outer radius of the chart.
    /// </summary>
    /// <param name="radius">If string and ending with '%', percentage of the maximum radius. number is considered to be pixels.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified radius.</returns>
    public TDataSet WithRadius(object radius) =>
        This with { Radius = radius };

    /// <summary>
    /// Sets the starting angle to draw arcs from.
    /// </summary>
    /// <param name="rotation">The starting angle to draw arcs from.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified rotation.</returns>
    public TDataSet WithRotation(int? rotation) =>
        This with { Rotation = rotation };

    /// <summary>
    /// Sets the sweep to allow arcs to cover.
    /// </summary>
    /// <param name="circumference">The sweep to allow arcs to cover.</param>
    /// <returns>A new instance of <typeparamref name="TDataSet"/> with the specified circumference.</returns>
    public TDataSet WithCircumference(int? circumference) =>
        This with { Circumference = circumference };
}
