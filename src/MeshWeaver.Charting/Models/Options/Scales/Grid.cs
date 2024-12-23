namespace MeshWeaver.Charting.Models.Options.Scales
{
    public record Grid
    {
        /// <summary>
        /// If false, do not display grid lines for this axis.
        /// </summary>
        public bool? Display { get; init; }

        /// <summary>
        /// If true, gridlines are circular (on radar chart only).
        /// </summary>
        public bool? Circular { get; init; }

        /// <summary>
        /// Color of the grid lines.
        /// </summary>
        public IEnumerable<ChartColor> Color { get; init; }

        /// <summary>
        /// Length and spacing of dashes.
        /// </summary>
        public IEnumerable<int> BorderDash { get; init; }

        /// <summary>
        /// Offset for line dashes.
        /// </summary>
        public double? BorderDashOffset { get; init; }

        /// <summary>
        /// Stroke width of grid lines.
        /// </summary>
        public IEnumerable<int> LineWidth { get; init; }

        /// <summary>
        /// If true draw border on the edge of the chart.
        /// </summary>
        public bool? DrawBorder { get; init; }

        /// <summary>
        /// If true, draw lines on the chart area inside the axis lines. This is useful when there are multiple axes and you need to control which grid lines are drawn.
        /// </summary>
        public bool? DrawOnChartArea { get; init; }

        /// <summary>
        /// If true, draw lines beside the ticks in the axis area beside the chart.
        /// </summary>
        public bool? DrawTicks { get; init; }

        /// <summary>
        /// Length in pixels that the grid lines will draw into the axis area.
        /// </summary>
        public int? TickLength { get; init; }

        /// <summary>
        /// Stroke width of the grid line for the first index (index 0).
        /// </summary>
        public int? ZeroLineWidth { get; init; }

        /// <summary>
        /// Stroke color of the grid line for the first index (index 0).
        /// </summary>
        public ChartColor ZeroLineColor { get; init; }

        /// <summary>
        /// Length and spacing of dashes of the grid line for the first index (index 0).
        /// </summary>
        public IEnumerable<int> ZeroLineBorderDash { get; init; }

        /// <summary>
        /// Offset for line dashes of the grid line for the first index (index 0).
        /// </summary>
        public double? ZeroLineBorderDashOffset { get; init; }

        /// <summary>
        /// If true, labels are shifted to be between grid lines. This is used in the bar chart.
        /// </summary>
        public bool? Offset { get; init; }
    }
}
