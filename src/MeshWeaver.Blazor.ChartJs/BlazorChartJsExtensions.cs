using MeshWeaver.Charting;
using MeshWeaver.Charting.Models;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.ChartJs;

public static class BlazorChartJsExtensions
{
    public static LayoutClientConfiguration AddChartJs(this LayoutClientConfiguration config)
    {
        config.Hub.GetTypeRegistry().WithTypes(ChartTypes);
        return config.WithView<ChartControl, ChartView>();
    }

    private static readonly Type[] ChartTypes = [typeof(ChartControl)];
    //typeof(ChartModel).Assembly.GetTypes()
    //.Where(t => t.IsAssignableTo(typeof(ChartModel)) || t.IsAssignableTo(typeof(DataSet)) || t.IsAssignableTo(typeof(ChartControl)))
    //.ToArray();
}
