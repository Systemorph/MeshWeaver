using OpenSmc.GridModel;
using OpenSmc.Layout.Client;

namespace OpenSmc.Blazor.AgGrid;

public static class BlazorAgGridExtensions
{
    public static LayoutClientConfiguration AddAgGrid(this LayoutClientConfiguration config)
        => config.WithView<GridControl, AgGrid>();
}
