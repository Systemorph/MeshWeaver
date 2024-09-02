using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public abstract record ArrayChart<TChart, TDataSet>
    : Chart<TChart, TDataSet>
    where TChart : ArrayChart<TChart, TDataSet>
    where TDataSet : DataSet, new()
{
    protected ArrayChart(IReadOnlyCollection<TDataSet> dataSets, ChartType chartType)
        : base(dataSets, chartType)
    {
        if (Data.Labels is null)
        {
            var maxLen = Data.DataSets.Select(ds => ds.Data?.Count() ?? 0).DefaultIfEmpty(1).Max();

            Data = Data with
            {
                Labels = Enumerable.Range(1, maxLen).Select(i => i.ToString()).ToArray(),
            };
        }
    }
}
