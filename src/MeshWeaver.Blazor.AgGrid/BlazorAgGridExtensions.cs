using MeshWeaver.GridModel;
using MeshWeaver.Layout.Client;

namespace MeshWeaver.Blazor.AgGrid;

public static class BlazorAgGridExtensions
{
    public static LayoutClientConfiguration AddAgGrid(this LayoutClientConfiguration config)
        => config.WithView<GridControl, AgGrid>();
}
