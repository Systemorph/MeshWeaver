using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public record TimeChart : Chart<TimeChart, TimeLineDataSet>
{
    public TimeChart(IReadOnlyCollection<TimeLineDataSet> dataSets)
        : base(dataSets, ChartType.Line) { }
}
