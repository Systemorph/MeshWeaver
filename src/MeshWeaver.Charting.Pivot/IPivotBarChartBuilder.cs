using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Pivot;

public interface IPivotBarChartBuilder : IPivotChartBuilder
{
    new IPivotBarChartBuilder WithOptions(Func<PivotChartModel, PivotChartModel> postProcessor);
    IPivotBarChartBuilder WithChartBuilder(Func<Models.ChartModel, Models.ChartModel> builder);
    IPivotBarChartBuilder AsStackedWithScatterTotals(); // rename it something better, and/or split into several methods
    IPivotChartBuilder WithRowsAsLine(params string[] rowLinesNames);
}
