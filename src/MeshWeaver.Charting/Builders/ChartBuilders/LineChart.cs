using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record LineChart
    : ArrayChart<LineChart, LineDataSet>
{
    public LineChart(IReadOnlyCollection<LineDataSet> dataSets) : base(dataSets, ChartType.Line) { }
}
