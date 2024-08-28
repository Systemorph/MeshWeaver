using System.Collections.Immutable;
using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Helpers;

namespace MeshWeaver.Charting.Builders.Options;

public record WaterfallChartOptions : WaterfallChartOptions<WaterfallChartOptions, FloatingBarDataSetBuilder>;

public record HorizontalWaterfallChartOptions : WaterfallChartOptions<HorizontalWaterfallChartOptions, HorizontalFloatingBarDataSetBuilder>;

public record WaterfallChartOptions<TOptions, TDataSetBuilder>
    where TOptions : WaterfallChartOptions<TOptions, TDataSetBuilder>
{
    internal string IncrementsLabel { get; init; } = ChartConst.Hidden;
    internal string DecrementsLabel { get; init; } = ChartConst.Hidden;
    internal string TotalLabel { get; init; } = ChartConst.Hidden;

    internal bool IncludeConnectors { get; init; }

    internal ImmutableHashSet<int> TotalIndexes { get; init; } = [];

    internal Func<TDataSetBuilder, TDataSetBuilder> BarDataSetModifier { get; init; }

    internal bool HasLastAsTotal { get; init; }

    internal Func<LineDataSetBuilder, LineDataSetBuilder> ConnectorDataSetModifier { get; init; } = d => d.ThinLine();

    internal Func<WaterfallStylingBuilder, WaterfallStylingBuilder> StylingOptions { get; init; }

    private TOptions This => (TOptions)this;

    public TOptions WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
        => This with { IncrementsLabel = incrementsLabel, DecrementsLabel = decrementsLabel, TotalLabel = totalLabel, };

    public TOptions WithConnectors(Func<LineDataSetBuilder, LineDataSetBuilder> connectorLineModifier = null)
        => This with { ConnectorDataSetModifier = connectorLineModifier ?? ConnectorDataSetModifier, IncludeConnectors = true, };

    public TOptions WithTotalsAtPositions(IEnumerable<int> totalIndexes)
        => This with { TotalIndexes = TotalIndexes.Union(totalIndexes) };

    public TOptions WithTotalsAtPositions(params int[] totalIndexes)
        => WithTotalsAtPositions(totalIndexes.AsEnumerable());

    public TOptions WithBarDataSetOptions(Func<TDataSetBuilder, TDataSetBuilder> barDataSetModifier)
        => This with { BarDataSetModifier = barDataSetModifier, };

    public TOptions WithLastAsTotal() => This with { HasLastAsTotal = true, };

    public TOptions WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> styling)
        => This with { StylingOptions = styling, };
}
