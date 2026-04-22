// <meshweaver>
// Id: BusinessUnitLayoutAreas
// DisplayName: Business Unit Areas
// </meshweaver>

using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;

/// <summary>
/// Areas for displaying business unit details.
/// Overrides Children to show Analysis and Lines of Business sections.
/// </summary>
public static class BusinessUnitLayoutAreas
{
    public static LayoutDefinition AddBusinessUnitLayoutAreas(this LayoutDefinition layout) =>
        layout.WithView("Children", BuChildren);

    private static UiControl BuChildren(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return Controls.Stack
            .WithView(Controls.Markdown("### Analysis"))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery($"path:{hubPath} scope:children -nodeType:NodeType")
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(false)
                .WithShowLoadingIndicator(false)
                .WithRenderMode(MeshSearchRenderMode.Flat))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery($"path:{hubPath}/Analysis scope:children")
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(false)
                .WithShowLoadingIndicator(false)
                .WithRenderMode(MeshSearchRenderMode.Flat))
            .WithView(Controls.Markdown("### Lines of Business"))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery($"path:{hubPath}/LineOfBusiness scope:children")
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(false)
                .WithShowLoadingIndicator(false)
                .WithRenderMode(MeshSearchRenderMode.Flat));
    }
}
