using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record PieChart
    : ArrayChart<PieChart, PieDataSet>
{
    public PieChart(IReadOnlyCollection<PieDataSet> dataSets) : base(dataSets, ChartType.Pie) { }
}
