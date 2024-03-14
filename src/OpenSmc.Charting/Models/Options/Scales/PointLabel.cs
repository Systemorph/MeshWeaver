using OpenSmc.Charting.Helpers;

namespace OpenSmc.Charting.Models.Options.Scales
{
    // https://www.chartjs.org/docs/3.7.1/axes/radial/linear.html#point-label-options
    public record PointLabel
    {
        /// <summary>
        /// Background color of the point label.
        /// </summary>
        public ChartColor BackdropColor { get; init; }

        /// <summary>
        /// Padding of label backdrop.
        /// </summary>
        public object BackdropPadding { get; init; }

        /// <summary>
        /// If true, point labels are shown.
        /// </summary>
        public bool? Display { get; init; }

        /// <summary>
        /// Callback function to transform data labels to point labels. The default implementation simply returns the current string.
        /// </summary>
        public object Callback { get; init; }

        /// <summary>
        /// Color of label.
        /// </summary>
        public ChartColor Color { get; init; }

        public Font Font { get; init; }

        /// <summary>
        /// Padding between chart and point labels.
        /// </summary>
        public int? Padding { get; init; }

        /// <summary>
        /// If true, point labels are centered.
        /// </summary>
        public bool? CenterPointLabels { get; init; }
    }
}
