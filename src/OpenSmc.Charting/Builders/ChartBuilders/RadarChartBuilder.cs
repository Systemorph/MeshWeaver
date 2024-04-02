using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public record RadarChartBuilder(Chart ChartModel = null, RangeOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<RadarChartBuilder, RadarDataSet, RangeOptionsBuilder, RadarDataSetBuilder>(ChartModel ?? new Chart(ChartType.Radar), OptionsBuilder)
{
    public RadarChartBuilder() : this(new Chart(ChartType.Radar)) { }
}