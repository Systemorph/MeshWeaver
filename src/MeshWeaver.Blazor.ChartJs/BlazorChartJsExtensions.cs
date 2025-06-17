using MeshWeaver.Charting;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.ChartJs;

public static class BlazorChartJsExtensions
{
    public static MessageHubConfiguration AddChartJs(this MessageHubConfiguration config)
    {
        return config.WithTypes(ChartTypes)
                .AddViews(layout => layout.WithView<ChartControl, ChartView>())
            ;
    }

    private static readonly Type[] ChartTypes = [typeof(ChartControl)];
}
