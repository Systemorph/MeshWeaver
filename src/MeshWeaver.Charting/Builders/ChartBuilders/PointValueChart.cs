using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record PointValueChart : Chart<PointValueChart, BubbleDataSet>
{
    public PointValueChart(IReadOnlyCollection<BubbleDataSet> dataSets)
        : base(dataSets, ChartType.Bubble) { }
}
