using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Segmented;

namespace MeshWeaver.Charting.Builders.Chart;

public record DoughnutChart
    : ArrayChart
{
    public DoughnutChart(IReadOnlyCollection<DoughnutDataSet> dataSets) : base(dataSets, ChartType.Doughnut) { }
}
