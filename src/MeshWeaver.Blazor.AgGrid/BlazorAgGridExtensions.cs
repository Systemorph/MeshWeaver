using MeshWeaver.GridModel;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.AgGrid;

public static class BlazorAgGridExtensions
{
    public static MessageHubConfiguration AddAgGrid(this MessageHubConfiguration config) =>
        config.WithType(typeof(GridControl))
            .AddViews(layout => layout.WithView<GridControl, AgGrid>())
        ;
}
