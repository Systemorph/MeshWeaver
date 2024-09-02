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
        AutoLabels = true;
        Data = Data with { Labels = GetUpdatedLabels(), };
    }
}
