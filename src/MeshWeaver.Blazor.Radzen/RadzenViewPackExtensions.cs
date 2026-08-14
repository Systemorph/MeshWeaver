using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Radzen;

/// <summary>
/// The Radzen view pack's single hub-side entry point. A view pack is a plain class library:
/// component types plus this registration — no routable pages, no shell (App.razor) tags (the
/// views self-load their static assets, see <see cref="RadzenViewBase{TControl,TView}"/>). The
/// DI-side twin is <see cref="RadzenServiceExtensions.AddRadzenServices"/>; together they are the
/// pack's complete surface, which is what makes it consumable via a ProjectReference today and via
/// boot-time assembly loading (<c>MeshBuilder.InstallAssemblies</c>) tomorrow without changes here.
/// </summary>
public static class RadzenViewPackExtensions
{
    /// <summary>
    /// Registers every Radzen-rendered control view on the hub configuration:
    /// charts (<c>ChartControl</c>) and the pivot grid (<c>PivotGridControl</c>).
    /// </summary>
    public static MessageHubConfiguration AddRadzenViews(this MessageHubConfiguration config) =>
        config.AddRadzenDataGrid().AddRadzenCharts();
}
