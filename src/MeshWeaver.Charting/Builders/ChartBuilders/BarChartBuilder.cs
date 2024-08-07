using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record BarChartBuilder(Chart ChartModel = null, BarOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<BarChartBuilder, BarDataSet, BarOptionsBuilder, BarDataSetBuilder>
        (ChartModel ?? new Chart(ChartType.Bar), OptionsBuilder)
{
    public BarChartBuilder() : this(new Chart(ChartType.Bar)) { }

    public BarChartBuilder AsHorizontalBar()
    {
        return WithOptions(options => options.WithIndexAxis("y"));
    }

    public bool IsHorizontal => OptionsBuilder.Options.IndexAxis == "y";
}
