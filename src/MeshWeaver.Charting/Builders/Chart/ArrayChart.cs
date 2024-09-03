using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public abstract record ArrayChart
    : Models.Chart
{
    protected ArrayChart(IReadOnlyCollection<DataSet> dataSets, ChartType chartType)
        : base(dataSets, chartType)
    {
        AutoLabels = true;
        Data = Data with { Labels = GetUpdatedLabels(), };
    }
}
