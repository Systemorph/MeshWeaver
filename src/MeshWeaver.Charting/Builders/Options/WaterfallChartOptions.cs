using MeshWeaver.Charting.Builders.OptionsBuilders;

namespace MeshWeaver.Charting.Builders.Options;

public record WaterfallChartOptions
{
    internal Func<WaterfallStylingBuilder, WaterfallStylingBuilder> StylingOptions { get; init; }

    public WaterfallChartOptions WithStylingOptions(Func<WaterfallStylingBuilder, WaterfallStylingBuilder> styling)
        => this with { StylingOptions = styling, };
}
