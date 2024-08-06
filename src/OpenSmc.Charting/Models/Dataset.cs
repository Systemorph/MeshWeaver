using System.Text.Json.Serialization;
using Newtonsoft.Json;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Helpers;
using OpenSmc.Charting.Models.Options;

namespace OpenSmc.Charting.Models
{
    // https://www.chartjs.org/docs/3.5.1/general/data-structures.html
    public abstract record DataSet
    {
        /// <summary>
        /// The fill color under the line.
        /// ChartColor or IEnumerable&lt;ChartColor&gt;
        /// </summary>
        public object BackgroundColor { get; init; }

        /// <summary>
        /// The color of the line.
        /// </summary>
        public IEnumerable<ChartColor> BorderColor { get; init; }

        /// <summary>
        /// The width of the line in pixels.
        /// </summary>
        public object BorderWidth { get; init; }

        /// <summary>
        /// How to clip relative to chartArea. Positive value allows overflow, negative value clips that many pixels inside chartArea. 0 = clip at chartArea. Clipping can also be configured per side: clip: {left: 5, top: false, right: -2, bottom: 0}
        /// </summary>
        public object Clip { get; init; }

        /// <summary>
        /// The data to plot in a line.
        /// </summary>
        public IEnumerable<object> Data { get; init; }

        /// <summary>
        /// Point background color when hovered.
        /// ChartColor or IEnumerable&lt;ChartColor&gt;
        /// </summary>
        public object HoverBackgroundColor { get; init; }

        /// <summary>
        /// Point border color when hovered.
        /// </summary>
        public IEnumerable<ChartColor> HoverBorderColor { get; init; }

        /// <summary>
        /// The label for the dataset which appears in the legend and tooltips.
        /// </summary>
        public string Label { get; init; }

        [JsonPropertyName("datalabels")]
        public DataLabels DataLabels { get; set; }

        /// <summary>
        /// How to parse the dataset. The parsing can be disabled by specifying parsing: false at chart options or dataset. If parsing is disabled, data must be sorted and in the formats the associated chart type and scales use internally.
        /// </summary>
        public virtual Parsing Parsing { get; init; }

        /// <summary>
        /// Start DataSet Disabled if set to True
        /// </summary>
        public bool? Hidden { get; init; }

        public ChartType? Type { get; init; }

        internal virtual bool HasLabel() => Label != null;
    }

    public record Parsing(string XAxisKey, string YAxisKey);

    internal record TimePointData
    {
        public string X { get; init; }
        public double? Y { get; init; }
    }

    internal record PointData
    {
        public double? X { get; init; }
        public double? Y { get; init; }
    }

    internal record BubbleData
    {
        public double X { get; init; }

        public double Y { get; init; }

        public double R { get; init; }
    }
}
