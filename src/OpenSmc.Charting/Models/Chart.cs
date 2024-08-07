using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models.Options;

namespace OpenSmc.Charting.Models
{
    public record Chart(ChartType Type)
    {
        /// <summary>
        /// Chart type e.g. bar, line, etc
        /// </summary>
        public ChartType Type { get; init; } = Type;

        /// <summary>
        /// Chart data
        /// </summary>
        public ChartData Data { get; init; } = new();

        /// <summary>
        /// Chart options configuration
        /// </summary>
        public ChartOptions Options { get; init; } = new();
    }
}
