using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record PieChartBuilder(Chart ChartModel = null, PieOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<PieChartBuilder, PieDataSet, PieOptionsBuilder, PieDataSetBuilder>(ChartModel ?? new Chart(ChartType.Pie), OptionsBuilder)
{
    public PieChartBuilder() : this(new Chart(ChartType.Pie)) { }
}