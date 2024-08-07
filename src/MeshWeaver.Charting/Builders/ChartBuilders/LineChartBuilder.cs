using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Builders.ChartBuilders;

public record LineChartBuilder(Chart ChartModel = null, LineOptionsBuilder OptionsBuilder = null)
    : ArrayChartBuilder<LineChartBuilder, LineDataSet, LineOptionsBuilder, LineDataSetBuilder>(ChartModel ?? new Chart(ChartType.Line), OptionsBuilder)
{
    public LineChartBuilder() : this(new Chart(ChartType.Line)) { }
}