using MeshWeaver.Charting.Helpers;

namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    public record MinorTick
    {
        /// <summary>
        /// Returns the string representation of the tick value as it should be displayed on the chart.
        /// </summary>
        public object Callback { get; init; }

        /// <summary>
        /// Font color for tick labels.
        /// </summary>
        public ChartColor FontColor { get; init; }

        /// <summary>
        /// Font family for the tick labels, follows CSS font-family options.

        /// </summary>
        public string FontFamily { get; init; }

        /// <summary>
        /// Font size for the tick labels.
        /// </summary>
        public int? FontSize { get; init; }

        /// <summary>
        /// Font style for the tick labels, follows CSS font-style options (i.e. normal, italic, oblique, initial, inherit).
        /// </summary>
        public string FontStyle { get; init; }
    }
}
