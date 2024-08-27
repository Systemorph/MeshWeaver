using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;
using MeshWeaver.Charting.Helpers;

namespace MeshWeaver.Charting.Builders.Options;

public record WaterfallChartOptions
{
    internal string IncrementsLabel { get; init; } = ChartConst.Hidden;
    internal string DecrementsLabel { get; init; } = ChartConst.Hidden;
    internal string TotalLabel { get; init; } = ChartConst.Hidden;

    internal bool IncludeConnectors { get; init; }

    internal bool HasLastAsTotal { get; init; }

    internal Func<LineDataSetBuilder, LineDataSetBuilder> ConnectorDataSetModifier { get; init; } = d => d.ThinLine();

    internal Func<WaterfallStylingBuilder, WaterfallStylingBuilder> StylingOptions { get; init; }

    public WaterfallChartOptions WithLegendItems(string incrementsLabel = null, string decrementsLabel = null, string totalLabel = null)
        => this with { IncrementsLabel = incrementsLabel, DecrementsLabel = decrementsLabel, TotalLabel = totalLabel, };

    public WaterfallChartOptions WithConnectors(Func<LineDataSetBuilder, LineDataSetBuilder> connectorLineModifier = null)
        => this with { ConnectorDataSetModifier = connectorLineModifier ?? ConnectorDataSetModifier, IncludeConnectors = true, };

    public WaterfallChartOptions WithLastAsTotal() => this with { HasLastAsTotal = true, };

    public WaterfallChartOptions WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> styling)
        => this with { StylingOptions = styling, };
}
