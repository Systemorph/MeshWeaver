using MeshWeaver.Charting.Builders.Chart;
using MeshWeaver.Charting.Builders.Options;
using MeshWeaver.Charting.Enums;
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
    public static Models.Chart FloatingBar(IReadOnlyCollection<FloatingBarDataSet> dataSets) => new(dataSets, ChartType.Bar);
    public static Models.Chart HorizontalFloatingBar(IReadOnlyCollection<HorizontalFloatingBarDataSet> dataSets)
        => new Models.Chart(dataSets, ChartType.Bar).WithOptions(o => o.WithIndexAxis("y"));
    public static Models.Chart Waterfall(List<double> deltas, Func<WaterfallChartOptions, WaterfallChartOptions> options = null)
        => Bar([]).ToWaterfallChart(deltas, options);
    public static Models.Chart HorizontalWaterfall(List<double> deltas, Func<HorizontalWaterfallChartOptions, HorizontalWaterfallChartOptions> options = null)
        => Bar([]).ToHorizontalWaterfallChart(deltas, options);
    public static Models.Chart Scatter(IReadOnlyCollection<LineScatterDataSet> dataSets) => new(dataSets, ChartType.Scatter);
    public static Models.Chart Bubble(IReadOnlyCollection<BubbleDataSet> dataSets) => new(dataSets, ChartType.Bubble);
    public static Models.Chart TimeLine(IReadOnlyCollection<TimeLineDataSet> dataSets) => new(dataSets, ChartType.Line);
}
