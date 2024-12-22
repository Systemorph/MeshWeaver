using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Waterfall;

namespace MeshWeaver.Charting.Pivot;

public interface IPivotWaterfallChartBuilder : IPivotChartBuilder
{
    IPivotWaterfallChartBuilder WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null);
    IPivotWaterfallChartBuilder WithStylingOptions(Func<WaterfallStyling, WaterfallStyling> func);
    IPivotWaterfallChartBuilder WithRangeOptionsBuilder(Func<ChartOptions, ChartOptions> func);

    IPivotWaterfallChartBuilder WithTotals(Func<PivotElementDescriptor, bool> filter);
    IPivotWaterfallChartBuilder WithConnectors();
}
