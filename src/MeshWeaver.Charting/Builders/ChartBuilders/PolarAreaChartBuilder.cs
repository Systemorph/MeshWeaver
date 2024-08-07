using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record PolarAreaChartBuilder(Chart ChartModel = null, PolarOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<PolarAreaChartBuilder, PolarDataSet, PolarOptionsBuilder, PolarDataSetBuilder>(ChartModel ?? new Chart(ChartType.PolarArea), OptionsBuilder)
{
    public PolarAreaChartBuilder() : this(new Chart(ChartType.PolarArea)) { }
}