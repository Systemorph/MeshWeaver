using MeshWeaver.Domain;
using MeshWeaver.GridModel;
using MeshWeaver.Layout.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.AgGrid;

public static class BlazorAgGridExtensions
{
    public static LayoutClientConfiguration AddAgGrid(this LayoutClientConfiguration config)
    {
        config.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetOrAddType(typeof(GridControl));
        return config.WithView<GridControl, AgGrid>();
    }
}
