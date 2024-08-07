using MeshWeaver.Charting.Helpers;

namespace MeshWeaver.Charting.Models.Options.Scales
{
    // https://www.chartjs.org/docs/3.7.1/axes/radial/linear.html#angle-line-options
    public record AngleLine
    {
        /// <summary>
        /// If true, angle lines are shown.
        /// </summary>
        public bool? Display { get; init; }

        /// <summary>
        /// Color of angled lines.
        /// </summary>
        public ChartColor Color { get; init; }

        /// <summary>
        /// Width of angled lines.
        /// </summary>
        public int? LineWidth { get; init; }

        /// <summary>
        /// Length and spacing of dashes on angled lines.
        /// </summary>
        public IEnumerable<int> BorderDash { get; init; }

        /// <summary>
        /// Offset for line dashes.
        /// </summary>
        public int? BorderDashOffset { get; init; }
    }
}
