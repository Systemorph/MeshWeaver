using OpenSmc.Charting.Enums;

namespace OpenSmc.Charting.Models
{
    public record Chart(ChartType Type)
    {
        /// <summary>
        /// Chart type determines the main type of the chart.
        /// </summary>
        public ChartType Type { get; init; } = Type;

        public Data Data { get; init; } = new();

        public Options.Options Options { get; init; } = new();

        /// <summary>
        /// Inline plugins
        /// </summary>
        public string Plugins { get; init; }
    }
}
