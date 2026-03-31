using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Radzen;

public static class RadzenChartExtensions
{
    public static MessageHubConfiguration AddRadzenCharts(this MessageHubConfiguration config) =>
        config.WithType(typeof(ChartControl))
            .AddViews(layout => layout.WithView<ChartControl, RadzenChartView>());
}
