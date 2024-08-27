using MeshWeaver.Charting.Builders.DataSetBuilders;
using MeshWeaver.Charting.Builders.OptionsBuilders;

namespace MeshWeaver.Charting.Builders.Options;

public record WaterfallChartOptions
{
    internal bool IncludeConnectors { get; init; }

    internal Func<LineDataSetBuilder, LineDataSetBuilder> ConnectorDataSetModifier { get; init; } = d => d.ThinLine();

    internal Func<WaterfallStylingBuilder, WaterfallStylingBuilder> StylingOptions { get; init; }

    public WaterfallChartOptions WithConnectors(Func<LineDataSetBuilder, LineDataSetBuilder> connectorLineModifier = null)
        => this with { ConnectorDataSetModifier = connectorLineModifier ?? ConnectorDataSetModifier, IncludeConnectors = true, };

    public WaterfallChartOptions WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> styling)
        => this with { StylingOptions = styling, };
}
