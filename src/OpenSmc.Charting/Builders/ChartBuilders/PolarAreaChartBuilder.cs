using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;
using Systemorph.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public record PolarAreaChartBuilder(Chart ChartModel = null, PolarOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<PolarAreaChartBuilder, PolarDataSet, PolarOptionsBuilder, PolarDataSetBuilder>(ChartModel ?? new Chart(ChartType.PolarArea), OptionsBuilder)
{
    public PolarAreaChartBuilder() : this(new Chart(ChartType.PolarArea)) { }
}