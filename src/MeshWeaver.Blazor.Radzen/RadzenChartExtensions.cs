using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Radzen;

/// <summary>
/// Extension methods for registering the Radzen-based chart view with a message hub.
/// </summary>
public static class RadzenChartExtensions
{
    /// <summary>
    /// Registers the <c>ChartControl</c> type and its Radzen view renderer on the hub configuration.
    /// </summary>
    /// <param name="config">The message hub configuration to extend.</param>
    /// <returns>The same configuration, for chaining.</returns>
    public static MessageHubConfiguration AddRadzenCharts(this MessageHubConfiguration config) =>
        config.WithType(typeof(ChartControl))
            .AddViews(layout => layout.WithView<ChartControl, RadzenChartView>());
}
