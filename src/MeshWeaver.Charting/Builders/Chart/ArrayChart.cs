using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public abstract record ArrayChart<TChart, TDataSet>
    : Chart<TChart, TDataSet>
    where TChart : ArrayChart<TChart, TDataSet>
    where TDataSet : DataSet, new()
{
    protected ArrayChart(IReadOnlyCollection<TDataSet> dataSets, ChartType chartType)
        : base(dataSets, chartType) { }

    public override Models.Chart ToChart()
    {
        var chart = base.ToChart();

        if (chart.Data.Labels is null)
        {
            var maxLen = chart.Data.DataSets.Select(ds => ds.Data?.Count() ?? 0).DefaultIfEmpty(1).Max();

            chart = chart with
            {
                Data = chart.Data with
                {
                    Labels = Enumerable.Range(1, maxLen).Select(i => i.ToString()).ToArray(),
                }
            };
        }

        return chart;
    }
}
