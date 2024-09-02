using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Models
{
    public record Chart
    {
        public Chart(IReadOnlyCollection<DataSet> dataSets, ChartType type)
        {
            Type = type;
            Data = Data.WithDataSets(dataSets);
        }

        /// <summary>
        /// Chart type e.g. bar, line, etc
        /// </summary>
        public ChartType Type { get; init; }

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
