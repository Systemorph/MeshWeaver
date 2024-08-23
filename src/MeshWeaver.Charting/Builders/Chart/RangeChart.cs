using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public abstract record RangeChart<TChart, TDataSet> : Chart<TChart, TDataSet>
    where TChart : Chart<TChart, TDataSet>
    where TDataSet : DataSet, IDataSetWithStack, new()
{
    protected RangeChart(IReadOnlyCollection<TDataSet> dataSets, ChartType chartType)
        : base(dataSets, chartType) { }
}
