using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public record PieChart
    : ArrayChart
{
    public PieChart(IReadOnlyCollection<PieDataSet> dataSets) : base(dataSets, ChartType.Pie) { }
}
