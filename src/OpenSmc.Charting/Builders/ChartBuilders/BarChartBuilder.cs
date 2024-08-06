using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public record BarChartBuilder(Chart ChartModel = null, BarOptionsBuilder OptionsBuilder = null)
    : BarChartBuilderBase<BarChartBuilder, BarDataSet, BarOptionsBuilder, BarDataSetBuilder>(ChartModel ?? new Chart(ChartType.Bar), OptionsBuilder)
{
    public BarChartBuilder() : this(new Chart(ChartType.Bar)) { }

    public BarChartBuilder AsHorizontalBar()
    {
        return WithOptions(options => options.WithIndexAxis("y"));
    }
}
