namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    // https://www.chartjs.org/docs/3.7.1/axes/cartesian/#common-tick-options-to-all-cartesian-axes
    public record CartesianTick : Tick
    {
        /// <summary>
        /// The tick alignment along the axis. Can be 'start', 'center', or 'end'.
        /// </summary>
        public string Align { get; init; }

        /// <summary>
        /// The tick alignment perpendicular to the axis. Can be 'near', 'center', or 'far'.
        /// </summary>
        public string CrossAlign { get; init; }

        /// <summary>
        /// The number of ticks to examine when deciding how many labels will fit. Setting a smaller value will be faster, but may be less accurate when there is large variability in label length.
        /// </summary>
        public int? SampleSize { get; init; }

        /// <summary>
        /// If true, automatically calculates how many labels can be shown and hides labels accordingly. Labels will be rotated up to maxRotation before skipping any. Turn autoSkip off to show all labels no matter what.
        /// </summary>
        public bool? AutoSkip { get; init; }

        /// <summary>
        /// Padding between the ticks on the horizontal axis when autoSkip is enabled.
        /// </summary>
        public int? AutoSkipPadding { get; init; }

        /// <summary>
        /// Should the defined min and max values be presented as ticks even if they are not "nice".
        /// </summary>
        public bool? IncludeBounds { get; init; }

        /// <summary>
        /// Distance in pixels to offset the label from the centre point of the tick (in the x-direction for the x-axis, and the y-direction for the y-axis). Note: this can cause labels at the edges to be cropped by the edge of the canvas.
        /// </summary>
        public string LabelOffset { get; init; }

        /// <summary>
        /// Maximum rotation for tick labels when rotating to condense labels. Note: Rotation doesn't occur until necessary. Note: Only applicable to horizontal scales.
        /// </summary>
        public int? MaxRotation { get; init; }

        /// <summary>
        /// Minimum rotation for tick labels. Note: Only applicable to horizontal scales.
        /// </summary>
        public int? MinRotation { get; init; }

        /// <summary>
        /// Flips tick labels around axis, displaying the labels inside the chart instead of outside. Note: Only applicable to vertical scales.
        /// </summary>
        public bool? Mirror { get; init; }
    }
}
