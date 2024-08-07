using MeshWeaver.Charting.Builders.ChartBuilders;

namespace MeshWeaver.Charting.Pivot;

public interface IPivotBarChartBuilder : IPivotChartBuilder
{
    IPivotBarChartBuilder WithChartBuilder(Func<BarChartBuilder, BarChartBuilder> builder);
    IPivotBarChartBuilder AsStackedWithScatterTotals(); // rename it something better, and/or split into several methods
    IPivotChartBuilder WithRowsAsLine(params string[] rowLinesNames);
}
