using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.Charting.Models
{
    public record ChartData
    {
        // ReSharper disable once StringLiteralTypo
        [JsonPropertyName("datasets")]
        public IReadOnlyCollection<DataSet> DataSets { get; init; }

        public IReadOnlyCollection<string> Labels { get; internal set; }

        public IEnumerable<string> XLabels { get; init; }

        public IEnumerable<string> YLabels { get; init; }

        public ChartData WithLabels(IReadOnlyCollection<string> labels) => this with { Labels = labels };

        public ChartData WithLabels(params string[] labels) => this with { Labels = labels };

        public ChartData WithDataSets(IEnumerable<DataSet> dataSets) => this with { DataSets = (DataSets == null ? dataSets : DataSets.Concat(dataSets)).ToImmutableList(), };

        public ChartData WithDataSets(params DataSet[] dataSets) => WithDataSets(dataSets.AsEnumerable());
    }
}
