using MeshWeaver.Charting;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.ChartJs;

public static class BlazorChartJsExtensions
{
    public static LayoutClientConfiguration AddChartJs(this LayoutClientConfiguration config)
    {
        config.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(typeof(ChartControl));
        return config.WithView<ChartControl, ChartView>();
    }
}
