using MeshWeaver.Charting.Builders.OptionsBuilders;

namespace MeshWeaver.Charting.Pivot;

public interface IPivotLineChartBuilder : IPivotArrayChartBuilder
{
    IPivotLineChartBuilder WithRangeOptionsBuilder(Func<LineOptionsBuilder, LineOptionsBuilder> func);
}