using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Pivot;

public interface IPivotLineChartBuilder : IPivotArrayChartBuilder
{
    IPivotLineChartBuilder WithRangeOptionsBuilder(Func<ChartOptions, ChartOptions> func);
}
