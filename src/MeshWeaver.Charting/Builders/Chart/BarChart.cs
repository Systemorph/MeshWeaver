using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public record BarChart
    : ArrayChart
{
    public BarChart(IReadOnlyCollection<BarDataSet> dataSets) : base(dataSets, ChartType.Bar) { }
}
