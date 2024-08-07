using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record RadarChartBuilder(Chart ChartModel = null, RangeOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<RadarChartBuilder, RadarDataSet, RangeOptionsBuilder, RadarDataSetBuilder>(ChartModel ?? new Chart(ChartType.Radar), OptionsBuilder)
{
    public RadarChartBuilder() : this(new Chart(ChartType.Radar)) { }
}