using OpenSmc.Charting.Builders.OptionsBuilders;

namespace OpenSmc.Charting.Pivot;

public interface IPivotLineChartBuilder : IPivotArrayChartBuilder
{
    IPivotLineChartBuilder WithRangeOptionsBuilder(Func<LineOptionsBuilder, LineOptionsBuilder> func);
}