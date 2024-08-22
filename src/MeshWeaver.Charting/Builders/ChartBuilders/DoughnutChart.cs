using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Segmented;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record DoughnutChart
    : ArrayChart<DoughnutChart, DoughnutDataSet>
{
    public DoughnutChart(IReadOnlyCollection<DoughnutDataSet> dataSets) : base(dataSets, ChartType.Doughnut) { }
}
