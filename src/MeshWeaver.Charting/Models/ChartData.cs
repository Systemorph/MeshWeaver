using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.Charting.Models;

public record ChartData
{
    // ReSharper disable once StringLiteralTypo
    [JsonPropertyName("datasets")] public ImmutableList<DataSet> DataSets { get; init; } = [];

    public IReadOnlyCollection<string> Labels { get; internal set; }

    public IEnumerable<string> XLabels { get; init; }

    public IEnumerable<string> YLabels { get; init; }

    public ChartData WithLabels(IReadOnlyCollection<string> labels) => this with { Labels = labels };

    public ChartData WithLabels(params string[] labels) => this with { Labels = labels };

    public ChartData WithDataSets(IEnumerable<DataSet> dataSets)
    {
        return (this with { DataSets = DataSets.AddRange(dataSets) }).WithAutoUpdatedLabels();
    }


    public ChartData WithDataSets(params DataSet[] dataSets) => WithDataSets(dataSets.AsEnumerable());




    private bool AutoLabels => DataSets.Count > 1;
    protected IReadOnlyCollection<string> GetUpdatedLabels()
    {
        if (AutoLabels && DataSets.Count > 0)
        {
            var maxLen = DataSets.Select(ds => ds.Data?.Count() ?? 0).DefaultIfEmpty(1).Max();

            return Enumerable.Range(1, maxLen).Select(i => i.ToString()).ToArray();
        }
        return Labels;
    }

    internal ChartData WithAutoUpdatedLabels() => this with { Labels = GetUpdatedLabels(), };

}
