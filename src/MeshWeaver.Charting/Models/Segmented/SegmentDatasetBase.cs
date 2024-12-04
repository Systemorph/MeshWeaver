namespace MeshWeaver.Charting.Models.Segmented
{
    public abstract record SegmentDataSetBase : DataSet
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
    }
}
