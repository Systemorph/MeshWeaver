using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public record RadarChart
    : ArrayChart
{
    public RadarChart(IReadOnlyCollection<RadarDataSet> dataSets) : base(dataSets, ChartType.Radar) { }
}
