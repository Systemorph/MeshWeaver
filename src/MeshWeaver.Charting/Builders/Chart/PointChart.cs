using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public record PointChart : Chart<PointChart, LineScatterDataSet>
{
    public PointChart(IReadOnlyCollection<LineScatterDataSet> dataSets)
        : base(dataSets, ChartType.Scatter) { }
}
