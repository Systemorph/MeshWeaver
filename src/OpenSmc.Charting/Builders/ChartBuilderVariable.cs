using OpenSmc.Charting.Builders.ChartBuilders;

namespace OpenSmc.Charting.Builders;

public interface IChartBuilderVariable
{
    BarChartBuilder Bar();
    HorizontalBarChartBuilder HorizontalBar();
    DoughnutChartBuilder Doughnut();
    LineChartBuilder Line();
    PieChartBuilder Pie();
    PolarAreaChartBuilder PolarArea();
    RadarChartBuilder Radar();
    FloatingBarChartBuilder FloatingBar();
    HorizontalFloatingBarChartBuilder HorizontalFloatingBar();
    WaterfallChartBuilder Waterfall();
    HorizontalWaterfallChartBuilder HorizontalWaterfall();
    PointChartBuilder Scatter();
    PointValueChartBuilder Bubble();
    TimeChartBuilder TimeLine();
}

public record ChartBuilderVariable : IChartBuilderVariable
{
    public BarChartBuilder Bar() => new();
    public HorizontalBarChartBuilder HorizontalBar() => new();
    public DoughnutChartBuilder Doughnut() => new();
    public LineChartBuilder Line() => new();
    public PieChartBuilder Pie() => new();
    public PolarAreaChartBuilder PolarArea() => new();
    public RadarChartBuilder Radar() => new();
    public FloatingBarChartBuilder FloatingBar() => new();
    public HorizontalFloatingBarChartBuilder HorizontalFloatingBar() => new();
    public WaterfallChartBuilder Waterfall() => new();
    public HorizontalWaterfallChartBuilder HorizontalWaterfall() => new();
    public PointChartBuilder Scatter() => new();
    public PointValueChartBuilder Bubble() => new();
    public TimeChartBuilder TimeLine() => new();
}