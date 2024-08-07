using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Segmented;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record DoughnutChartBuilder(Chart ChartModel = null, DoughnutOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<DoughnutChartBuilder, DoughnutDataSet, DoughnutOptionsBuilder, DoughnutDataSetBuilder>(ChartModel ?? new Chart(ChartType.Doughnut), OptionsBuilder)
{
    public DoughnutChartBuilder() : this(new Chart(ChartType.Doughnut)) { }
}