using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public record HorizontalBarChartBuilder(Chart ChartModel = null, HorizontalBarOptionsBuilder OptionsBuilder = null)
    : BarChartBuilderBase<HorizontalBarChartBuilder, HorizontalBarDataSet, HorizontalBarOptionsBuilder, HorizontalBarDataSetBuilder>(ChartModel ?? new Chart(ChartType.Bar), OptionsBuilder)
{
    public HorizontalBarChartBuilder()
        : this(new Chart(ChartType.Bar))
    {
       OptionsBuilder = OptionsBuilder.WithIndexAxis("y");
    }
}
