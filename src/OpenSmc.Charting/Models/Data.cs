using System.Text.Json.Serialization;

namespace OpenSmc.Charting.Models
{
    public record Data
    {
        // ReSharper disable once StringLiteralTypo
        [JsonPropertyName("datasets")]
        public IEnumerable<DataSet> DataSets { get; init; }

        public IEnumerable<string> Labels { get; internal set; }

        public IEnumerable<string> XLabels { get; init; }

        public IEnumerable<string> YLabels { get; init; }

        public Data WithLabels(IEnumerable<string> labels) => this with { Labels = labels };

        public Data WithLabels(params string[] labels) => this with { Labels = labels };

        public Data WithDataSets(List<DataSet> dataSets) => this with { DataSets = DataSets == null ? dataSets : DataSets.Concat(dataSets).ToList() };

        public Data WithDataSets(params DataSet[] dataSets) => this with { DataSets = DataSets == null ? dataSets : DataSets.Concat(dataSets).ToList() };
    }
}
