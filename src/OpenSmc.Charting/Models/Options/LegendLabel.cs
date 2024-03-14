using OpenSmc.Charting.Helpers;

namespace OpenSmc.Charting.Models.Options
{
    public record LegendLabel
    {
        /// <summary>
        /// Width of coloured box.
        /// </summary>
        public int? BoxWidth { get; init; }

        /// <summary>
        /// Height of coloured box.
        /// </summary>
        public int? BoxHeight { get; init; }

        /// <summary>
        /// Color of label and the strikethrough.
        /// </summary>
        public ChartColor Color { get; init; }

        /// <summary>
        /// Font style inherited from global configuration.
        /// </summary>
        public Font Font { get; init; }

        /// <summary>
        /// Padding between rows of colored boxes.
        /// </summary>
        public object Padding { get; init; }

        /// <summary>
        /// Generates legend items for each thing in the legend. Default implementation returns the text + styling for the color box.
        /// </summary>
        public object GenerateLabels { get; init; }

        /// <summary>
        /// Filters legend items out of the legend. Receives 2 parameters, a Legend Item and the chart data.
        /// </summary>
        public object Filter { get; init; }

        /// <summary>
        /// Sorts legend items. Receives 3 parameters, two Legend Items and the chart data.
        /// </summary>
        public object Sort { get; init; }

        /// <summary>
        /// If specified, this style of point is used for the legend. Only used if usePointStyle is true.
        /// </summary>
        public string PointStyle { get; init; }

        /// <summary>
        /// Horizontal alignment of the label text. Options are: 'left', 'right' or 'center'.
        /// </summary>
        public string TextAlign { get; init; }

        /// <summary>
        /// Label style will match corresponding point style (size is based on fontSize, boxWidth is not used in this case).
        /// </summary>
        public bool? UsePointStyle { get; init; }
    }
}
