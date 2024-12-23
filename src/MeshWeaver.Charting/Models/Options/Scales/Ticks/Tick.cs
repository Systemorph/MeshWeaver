namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    // https://www.chartjs.org/docs/3.7.1/axes/#common-tick-options-to-all-axes
    public record Tick
    {
        /// <summary>
        /// Color of label backdrops.
        /// </summary>
        public ChartColor BackdropColor { get; init; }

        /// <summary>
        /// Padding of label backdrop.
        /// </summary>
        public string BackdropPadding { get; init; }

        /// <summary>
        /// Returns the string representation of the tick value as it should be displayed on the chart.
        /// </summary>
        public string Callback { get; init; }

        /// <summary>
        /// If true, show tick labels.
        /// </summary>
        public bool? Display { get; init; }

        /// <summary>
        /// Color of ticks.
        /// </summary>
        public ChartColor Color { get; init; }

        public Font Font { get; init; }

        public MajorTick Major { get; init; }

        /// <summary>
        /// Sets the offset of the tick labels from the axis.
        /// </summary>
        public int? Padding { get; init; }

        /// <summary>
        /// If true, draw a background behind the tick labels.
        /// </summary>
        public bool? ShowLabelBackdrop { get; init; }

        /// <summary>
        /// The color of the stroke around the text.
        /// </summary>
        public ChartColor TextStrokeColor { get; init; }

        /// <summary>
        /// Stroke width around the text.
        /// </summary>
        public int? TextStrokeWidth { get; init; }

        /// <summary>
        /// z-index of tick layer. Useful when ticks are drawn on chart area. Values &lt;= 0 are drawn under datasets, &gt; 0 on top.
        /// </summary>
        public string Z { get; init; }
    }
}
