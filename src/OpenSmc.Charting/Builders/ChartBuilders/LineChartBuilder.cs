using OpenSmc.Charting.Builders.DataSetBuilders;
using OpenSmc.Charting.Builders.OptionsBuilders;
using OpenSmc.Charting.Enums;
using OpenSmc.Charting.Models;

namespace OpenSmc.Charting.Builders.ChartBuilders;

public record LineChartBuilder(Chart ChartModel = null, LineOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<LineChartBuilder, LineDataSet, LineOptionsBuilder, LineDataSetBuilder>(ChartModel ?? new Chart(ChartType.Line), OptionsBuilder)
{
    public LineChartBuilder() : this(new Chart(ChartType.Line)) { }
}