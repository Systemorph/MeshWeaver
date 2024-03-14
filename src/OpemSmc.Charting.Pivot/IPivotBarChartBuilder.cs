namespace OpenSmc.Charting.Pivot;

public interface IPivotBarChartBuilder : IPivotChartBuilder
{
    IPivotBarChartBuilder AsStackedWithScatterTotals(); // rename it something better, and/or split into several methods
    IPivotChartBuilder WithRowsAsLine(params string[] rowLinesNames);
}