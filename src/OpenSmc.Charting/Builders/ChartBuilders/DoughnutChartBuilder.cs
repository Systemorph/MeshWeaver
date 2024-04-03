using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;
using OpenSmc.Charting.Models.Segmented;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public record DoughnutChartBuilder(Chart ChartModel = null, DoughnutOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<DoughnutChartBuilder, DoughnutDataSet, DoughnutOptionsBuilder, DoughnutDataSetBuilder>(ChartModel ?? new Chart(ChartType.Doughnut), OptionsBuilder)
{
    public DoughnutChartBuilder() : this(new Chart(ChartType.Doughnut)) { }
}