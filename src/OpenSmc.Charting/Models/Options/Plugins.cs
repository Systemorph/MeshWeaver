using System.Text.Json.Serialization;
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

        /// <summary>
        /// ColorSchemes plugin configuration.
        /// </summary>
        [JsonPropertyName("colorschemes")]
        public ColorSchemes ColorSchemes { get; init; } = new();

        /// <summary>
        /// DataLabels plugin configuration.
        /// </summary>
        [JsonPropertyName("datalabels")]
        public DataLabels DataLabels { get; set; }

        public Plugins WithLegend (Func<Legend, Legend> builder = null)
        {
            var legend = Legend ?? new Legend() {Display = true};

            if (builder != null)
            {
                legend = builder(legend);
            }

            return this with { Legend = legend };
        }
        
        public Plugins WithTitle (Func<Title, Title> builder = null)
        {
            var title = Title ?? new Title();

            if (builder != null)
            {
                title = builder(title);
            }

            return this with { Title = title };
        }

        public Plugins WithSubtitle (Func<Title, Title> builder = null)
        {
            var subtitle = Subtitle ?? new Title();
            
            if (builder != null)
            {
                builder(subtitle);
            }

            return this with { Subtitle = subtitle };
        }

        public Plugins WithTooltip (Func<ToolTip, ToolTip> builder = null)
        {
            var tooltip = Tooltip ?? new ToolTip() {Enabled = true};

            if (builder != null)
            {
                tooltip = builder(tooltip);
            }

            return this with { Tooltip = tooltip };
        }

        public Plugins WithColorSchemes (Func<ColorSchemes, ColorSchemes> builder = null)
        {
            var colorSchemes = ColorSchemes ?? new ColorSchemes();

            if (builder != null)
            {
                colorSchemes = builder(colorSchemes);
            }

            return this with { ColorSchemes = colorSchemes };
        }

        public Plugins WithDataLabels (Func<DataLabels, DataLabels> builder = null)
        {
            var dataLabels = DataLabels ?? new DataLabels() { Display = true};
            
            if (builder != null)
            {
                dataLabels = builder(dataLabels);
            }

            return this with { DataLabels = dataLabels };
        }
    }
}
