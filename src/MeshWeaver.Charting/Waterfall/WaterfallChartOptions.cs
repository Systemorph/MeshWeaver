using System.Collections.Immutable;
using MeshWeaver.Charting.Helpers;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Line;

namespace MeshWeaver.Charting.Waterfall;

public record WaterfallChartOptions : WaterfallChartOptions<WaterfallChartOptions>;

public record HorizontalWaterfallChartOptions : WaterfallChartOptions<HorizontalWaterfallChartOptions>;

public record WaterfallChartOptions<TOptions>
    where TOptions : WaterfallChartOptions<TOptions>
{
    internal string IncrementsLabel { get; init; } = ChartConst.Hidden;
    internal string DecrementsLabel { get; init; } = ChartConst.Hidden;
    internal string TotalLabel { get; init; } = ChartConst.Hidden;

    internal bool IncludeConnectors { get; init; }

    internal ImmutableHashSet<int> TotalIndexes { get; init; } = [];

    internal Func<BarDataSet, BarDataSet> BarDataSetModifier { get; init; }

    internal bool HasLastAsTotal { get; init; }

    internal Func<LineDataSet, LineDataSet> ConnectorDataSetModifier { get; init; } = d => d.ThinLine();

    internal Func<WaterfallStyling, WaterfallStyling> StylingOptions { get; init; }

    internal ImmutableList<string> Labels { get; init; }

    private TOptions This => (TOptions)this;

    public TOptions WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
        => This with { IncrementsLabel = incrementsLabel, DecrementsLabel = decrementsLabel, TotalLabel = totalLabel, };

    public TOptions WithConnectors(Func<LineDataSet, LineDataSet> connectorLineModifier = null)
        => This with { ConnectorDataSetModifier = connectorLineModifier ?? ConnectorDataSetModifier, IncludeConnectors = true, };

    public TOptions WithTotalsAtPositions(IEnumerable<int> totalIndexes)
        => This with { TotalIndexes = TotalIndexes.Union(totalIndexes) };

    public TOptions WithTotalsAtPositions(params int[] totalIndexes)
        => WithTotalsAtPositions(totalIndexes.AsEnumerable());

    public TOptions WithBarDataSetOptions(Func<BarDataSet, BarDataSet> barDataSetModifier)
        => This with { BarDataSetModifier = barDataSetModifier, };

    public TOptions WithLastAsTotal() => This with { HasLastAsTotal = true, };

    public TOptions WithStylingOptions(Func<WaterfallStyling, WaterfallStyling> styling)
        => This with { StylingOptions = styling, };

    public TOptions WithLabels(IReadOnlyCollection<string> labels)
        => This with { Labels = labels.ToImmutableList(), };
}
