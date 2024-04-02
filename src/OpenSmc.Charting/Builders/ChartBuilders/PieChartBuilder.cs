using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public record PieChartBuilder(Chart ChartModel = null, PieOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<PieChartBuilder, PieDataSet, PieOptionsBuilder, PieDataSetBuilder>(ChartModel ?? new Chart(ChartType.Pie), OptionsBuilder)
{
    public PieChartBuilder() : this(new Chart(ChartType.Pie)) { }
}