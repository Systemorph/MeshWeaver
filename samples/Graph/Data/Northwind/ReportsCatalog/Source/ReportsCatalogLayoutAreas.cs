// <meshweaver>
// Id: ReportsCatalogLayoutAreas
// DisplayName: Reports Catalog Views
// </meshweaver>

using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

/// <summary>
/// Views for ReportsCatalog nodes — a catalog header over its reports as cards.
///
/// <para>Both views are TEMPLATES (Doc/GUI/DataBinding → "Templates first, data later"): built from
/// the node's PATH alone. The title is a pointer into the node, the report cards are a
/// <see cref="MeshSearchControl"/> whose query the GUI runs — each card reading its own report's
/// name, abstract and image — so the hub reads neither the catalog nor its children, and a report
/// added or edited later shows up without a reload.</para>
/// </summary>
public static class ReportsCatalogLayoutAreas
{
    /// <summary>
    /// Registers reports catalog views with the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddReportsCatalogLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("Thumbnail", Thumbnail);

    /// <summary>
    /// Overview view showing the catalog header and a card per report.
    /// </summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The overview template.</returns>
    public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
        => OverviewTemplate(host.Hub.Address.ToString());

    /// <summary>
    /// Thumbnail view for catalog display: the PATH alone — the thumbnail view reads the node's
    /// name, abstract and image itself, live.
    /// </summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The thumbnail, bound by path.</returns>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var path = host.Hub.Address.ToString();
        return new MeshNodeThumbnailControl(path, path);
    }

    /// <summary>The catalog at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The catalog node; its children are the reports.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl OverviewTemplate(string nodePath) =>
        Controls.Stack.WithWidth("100%")
            .WithStyle("max-width: 960px; margin: 0 auto; padding: 0 24px; gap: 24px;")
            .WithView(Controls.H1(new JsonPointerReference("name")) with
            {
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false)
            })
            .WithView(Controls.MeshSearch
                .WithHiddenQuery(ReportsQuery(nodePath))
                .WithShowSearchBox(false)
                .WithRenderMode(MeshSearchRenderMode.Flat));

    /// <summary>The reports of the catalog at <paramref name="nodePath"/>, in their authored order.</summary>
    /// <param name="nodePath">The catalog node.</param>
    /// <returns>The query the cards are listed by.</returns>
    public static string ReportsQuery(string nodePath) => $"namespace:{nodePath} sort:order";
}
