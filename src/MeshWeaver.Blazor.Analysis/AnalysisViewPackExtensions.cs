using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Analysis;

/// <summary>
/// The analysis view pack's single hub-side entry point: Blazor renderers for the standard
/// analysis controls (<see cref="KpiStripControl"/>, <see cref="TowerControl"/>,
/// <see cref="ComparisonBarsControl"/>). A plain view-pack class library — component types plus
/// this registration; the control records and their server-side geometry stay in
/// <c>MeshWeaver.Layout</c>. Register before <c>AddBlazor()</c> (packs registered later also work
/// since the fallback moved to its own slot, but earlier registration wins ties by design).
/// </summary>
public static class AnalysisViewPackExtensions
{
    /// <summary>
    /// Registers the three analysis control views on the hub configuration.
    /// </summary>
    public static MessageHubConfiguration AddAnalysisViews(this MessageHubConfiguration config) =>
        config
            .WithType(typeof(KpiStripControl))
            .WithType(typeof(TowerControl))
            .WithType(typeof(ComparisonBarsControl))
            .AddViews(layout => layout
                .WithView<KpiStripControl, KpiStripView>()
                .WithView<TowerControl, TowerView>()
                .WithView<ComparisonBarsControl, ComparisonBarsView>());
}
