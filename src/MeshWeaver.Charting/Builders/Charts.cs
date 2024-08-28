using MeshWeaver.Charting.Builders.Chart;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Segmented;

namespace MeshWeaver.Charting.Builders;

public static class Charts
{
    public static BarChart Bar(IReadOnlyCollection<BarDataSet> dataSets) => new(dataSets);
    public static DoughnutChart Doughnut(IReadOnlyCollection<DoughnutDataSet> dataSets) => new(dataSets);
    public static LineChart Line(IReadOnlyCollection<LineDataSet> dataSets) => new(dataSets);
    public static PieChart Pie(IReadOnlyCollection<PieDataSet> dataSets) => new(dataSets);
    public static PolarAreaChart PolarArea(IReadOnlyCollection<PolarDataSet> dataSets) => new(dataSets);
    public static RadarChart Radar(IReadOnlyCollection<RadarDataSet> dataSets) => new(dataSets);
    public static FloatingBarChart FloatingBar(IReadOnlyCollection<FloatingBarDataSet> dataSets) => new(dataSets);
    public static HorizontalFloatingBarChart HorizontalFloatingBar(IReadOnlyCollection<HorizontalFloatingBarDataSet> dataSets) => new(dataSets);
    public static PointChart Scatter(IReadOnlyCollection<LineScatterDataSet> dataSets) => new(dataSets);
    public static PointValueChart Bubble(IReadOnlyCollection<BubbleDataSet> dataSets) => new(dataSets);
    public static TimeChart TimeLine(IReadOnlyCollection<TimeLineDataSet> dataSets) => new(dataSets);
}
