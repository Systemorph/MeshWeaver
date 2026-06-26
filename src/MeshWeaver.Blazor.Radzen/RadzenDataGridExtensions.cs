using MeshWeaver.Layout;
using MeshWeaver.Layout.Pivot;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Radzen;

/// <summary>
/// Extension methods for registering the Radzen-based pivot grid view with a message hub.
/// </summary>
public static class RadzenDataGridExtensions
{
    /// <summary>
    /// Registers the <c>PivotGridControl</c> type and its Radzen view renderer on the hub configuration.
    /// </summary>
    /// <param name="config">The message hub configuration to extend.</param>
    /// <returns>The same configuration, for chaining.</returns>
    public static MessageHubConfiguration AddRadzenDataGrid(this MessageHubConfiguration config) =>
        config.WithType(typeof(PivotGridControl))
            .AddViews(layout => layout.WithView<PivotGridControl, RadzenPivotGridView>());
}
