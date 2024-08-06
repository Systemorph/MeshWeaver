using OpenSmc.Charting.Builders.ChartBuilders;

namespace OpenSmc.Charting.Pivot;

public interface IPivotBarChartBuilder : IPivotChartBuilder
{
    IPivotBarChartBuilder WithChartBuilder(Func<BarChartBuilder, BarChartBuilder> builder);
    IPivotBarChartBuilder AsStackedWithScatterTotals(); // rename it something better, and/or split into several methods
    IPivotChartBuilder WithRowsAsLine(params string[] rowLinesNames);
}
