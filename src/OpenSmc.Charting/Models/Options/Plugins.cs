using System.Text.Json.Serialization;
using Newtonsoft.Json;
using OpenSmc.Charting.Models.Options.Tooltips;

namespace OpenSmc.Charting.Models.Options
{
    public record Plugins
    {
        /// <summary>
        /// The chart legend displays data about the datasets that are appearing on the chart.
        /// </summary>
        public Legend Legend { get; internal set; }

        /// <summary>
        /// The chart title defines text to draw at the top of the chart.
        /// </summary>
        public Title Title { get; internal set; }

        /// <summary>
        /// Subtitle is a second title placed under the main title, by default.
        /// </summary>
        public Title Subtitle { get; internal set; }

        /// <summary>
        /// The global options for the chart tooltips.
        /// </summary>
        public ToolTip Tooltip { get; init; }

        [JsonPropertyName("colorschemes")]
        public ColorSchemes ColorSchemes { get; init; } = new();

        [JsonPropertyName("datalabels")]
        public DataLabels DataLabels { get; set; }
    }
}
