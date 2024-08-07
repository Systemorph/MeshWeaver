using MeshWeaver.Charting;
using MeshWeaver.Layout.Client;

namespace MeshWeaver.Blazor.ChartJs;

public static class BlazorChartJsExtensions
{
    public static LayoutClientConfiguration AddChartJs(this LayoutClientConfiguration config)
        => config.WithView<ChartControl, ChartView>();
}
