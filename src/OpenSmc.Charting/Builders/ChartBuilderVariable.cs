using OpenSmc.Charting.Builders.ChartBuilders;

namespace OpenSmc.Charting.Builders;

public static class ChartBuilder
{
    public static BarChartBuilder Bar() => new();
    public static HorizontalBarChartBuilder HorizontalBar() => new();
    public static DoughnutChartBuilder Doughnut() => new();
    public static LineChartBuilder Line() => new();
    public static PieChartBuilder Pie() => new();
    public static PolarAreaChartBuilder PolarArea() => new();
    public static RadarChartBuilder Radar() => new();
    public static FloatingBarChartBuilder FloatingBar() => new();
    public static HorizontalFloatingBarChartBuilder HorizontalFloatingBar() => new();
    public static WaterfallChartBuilder Waterfall() => new();
    public static HorizontalWaterfallChartBuilder HorizontalWaterfall() => new();
    public static PointChartBuilder Scatter() => new();
    public static PointValueChartBuilder Bubble() => new();
    public static TimeChartBuilder TimeLine() => new();
}