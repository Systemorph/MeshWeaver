using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.Chart;

public record FloatingBarChart
    : RangeChart<FloatingBarChart, FloatingBarDataSet>
{
    public FloatingBarChart(IReadOnlyCollection<FloatingBarDataSet> dataSets) : base(dataSets, ChartType.Bar) { }
}

public record HorizontalFloatingBarChart
    : RangeChart<HorizontalFloatingBarChart, HorizontalFloatingBarDataSet>
{
    public HorizontalFloatingBarChart(IReadOnlyCollection<HorizontalFloatingBarDataSet> dataSets)
        : base(dataSets, ChartType.Bar)
    {
        Options = Options.WithIndexAxis("y");
    }
}
