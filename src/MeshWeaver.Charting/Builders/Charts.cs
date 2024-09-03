using MeshWeaver.Charting.Builders.Chart;
using MeshWeaver.Charting.Builders.Options;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Segmented;

namespace MeshWeaver.Charting.Builders;

public static class Charts
{
    public static Models.Chart Bar(IReadOnlyCollection<BarDataSet> dataSets)
        => new Models.Chart(dataSets, ChartType.Bar).AsArrayChart();
    public static Models.Chart Doughnut(IReadOnlyCollection<DoughnutDataSet> dataSets)
        => new Models.Chart(dataSets, ChartType.Doughnut).AsArrayChart();
    public static Models.Chart Line(IReadOnlyCollection<LineDataSet> dataSets)
        => new Models.Chart(dataSets, ChartType.Line).AsArrayChart();
    public static Models.Chart Pie(IReadOnlyCollection<PieDataSet> dataSets)
        => new Models.Chart(dataSets, ChartType.Pie).AsArrayChart();
    public static Models.Chart PolarArea(IReadOnlyCollection<PolarDataSet> dataSets)
        => new Models.Chart(dataSets, ChartType.PolarArea).AsArrayChart();
    public static Models.Chart Radar(IReadOnlyCollection<RadarDataSet> dataSets)
        => new Models.Chart(dataSets, ChartType.Radar).AsArrayChart();
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

    private static Models.Chart AsArrayChart(this Models.Chart chart) => chart.WithAutoLabels().WithAutoUpdatedLabels();
}
