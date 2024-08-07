using System.Text.Json.Serialization;

namespace MeshWeaver.Charting.Models
{
    public record ChartData
    {
        // ReSharper disable once StringLiteralTypo
        [JsonPropertyName("datasets")]
        public IEnumerable<DataSet> DataSets { get; init; }

        public IEnumerable<string> Labels { get; internal set; }

        public IEnumerable<string> XLabels { get; init; }

        public IEnumerable<string> YLabels { get; init; }

        public ChartData WithLabels(IEnumerable<string> labels) => this with { Labels = labels };

        public ChartData WithLabels(params string[] labels) => this with { Labels = labels };

        public ChartData WithDataSets(List<DataSet> dataSets) => this with { DataSets = DataSets == null ? dataSets : DataSets.Concat(dataSets).ToList() };

        public ChartData WithDataSets(params DataSet[] dataSets) => this with { DataSets = DataSets == null ? dataSets : DataSets.Concat(dataSets).ToList() };
    }
}
