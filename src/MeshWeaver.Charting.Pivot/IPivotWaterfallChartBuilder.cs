using MeshWeaver.Charting.Builders.OptionsBuilders;

namespace MeshWeaver.Charting.Pivot;

public interface IPivotWaterfallChartBuilder : IPivotChartBuilder
{
    IPivotWaterfallChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null);
    IPivotWaterfallChartBuilder WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> func);
    IPivotWaterfallChartBuilder WithRangeOptionsBuilder(Func<RangeOptionsBuilder, RangeOptionsBuilder> func);

    IPivotWaterfallChartBuilder WithTotals(Func<PivotElementDescriptor, bool> filter);
    IPivotWaterfallChartBuilder WithConnectors();
}