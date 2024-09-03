using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public record LineChart
    : ArrayChart
{
    public LineChart(IReadOnlyCollection<LineDataSet> dataSets) : base(dataSets, ChartType.Line) { }
}
