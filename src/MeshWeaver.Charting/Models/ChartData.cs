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

    public ChartData WithLabels(params IEnumerable<string> labels) => 
        (this with { Labels = labels.ToArray(), AutoLabels = false })
        .WithAutoUpdatedLabels();


    public ChartData WithDataSets(params IEnumerable<DataSet> dataSets)
    {
        return (this with { DataSets = DataSets.AddRange(dataSets.Select(AdjustForChartType)) })
            .WithAutoUpdatedLabels();
    }


    private DataSet AdjustForChartType(DataSet ds)
    {
        var chartType = DataSets.FirstOrDefault()?.ChartType;
        if (chartType is null)
            return ds;
        return ds.Type is null && ds.ChartType !=  chartType ? ds with { Type = ds.ChartType } : ds;
    }

    private bool? AutoLabels { get; init; } 

    
    protected IReadOnlyCollection<string> GetUpdatedLabels()
    {
        if (AutoLabels == true || (AutoLabels is null && DataSets.Count > 1))
        {
            var maxLen = DataSets.Select(ds => ds.Data?.Count() ?? 0).DefaultIfEmpty(1).Max();

            return DataSets.Take(maxLen).Select((x,i) => x.Label ?? (i+1).ToString()).ToArray();
        }
        return Labels;
    }

    internal ChartData WithAutoUpdatedLabels() => this with { Labels = GetUpdatedLabels(), };

}
