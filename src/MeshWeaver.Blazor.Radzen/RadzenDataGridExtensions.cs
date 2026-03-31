using MeshWeaver.Layout;
using MeshWeaver.Layout.Pivot;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Radzen;

public static class RadzenDataGridExtensions
{
    public static MessageHubConfiguration AddRadzenDataGrid(this MessageHubConfiguration config) =>
        config.WithType(typeof(PivotGridControl))
            .AddViews(layout => layout.WithView<PivotGridControl, RadzenPivotGridView>());
}
