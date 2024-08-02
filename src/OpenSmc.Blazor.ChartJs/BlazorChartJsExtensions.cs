using OpenSmc.Charting;
using OpenSmc.Layout.Client;

namespace OpenSmc.Blazor.ChartJs;

public static class BlazorChartJsExtensions
{
    public static LayoutClientConfiguration AddChartJs(this LayoutClientConfiguration config)
        => config.WithView<ChartControl, ChartView>();
}
