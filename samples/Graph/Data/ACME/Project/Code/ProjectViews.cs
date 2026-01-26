// <meshweaver>
// Id: ProjectViews
// DisplayName: Project Views
// </meshweaver>

using MeshWeaver.Graph;

/// <summary>
/// Catalog views for Project nodes using MeshSearchControl with server-side grouping.
/// </summary>
public static class ProjectViews
{
    /// <summary>
    /// TodosByCategory view grouping items by category.
    /// Uses reactive ObserveMeshSearchByProperty for live updates with server-side grouping.
    /// </summary>
    [Display(GroupName = "Overview", Order = 1)]
    public static IObservable<UiControl?> TodosByCategory(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var query = $"path:{hubPath}/Todo nodeType:ACME/Project/Todo state:Active scope:subtree";

        return host.Hub.ObserveMeshSearchByProperty(
            query,
            groupByProperty: "Category",
            groupLabelSelector: key => $"\ud83d\udcc1 {key ?? "Uncategorized"}",
            groupOrderSelector: key => string.IsNullOrEmpty(key) ? 999 : 0
        ).Select(c => c
            .WithCollapsibleSections(true)
            .WithSectionCounts(true)
            .WithGridBreakpoints(xs: 12, sm: 6, md: 4));
    }

    /// <summary>
    /// Backlog view showing unassigned tasks grouped by priority.
    /// Uses reactive ObserveMeshSearchByProperty for live updates with server-side filtering and grouping.
    /// </summary>
    [Display(GroupName = "Planning", Order = 1)]
    public static IObservable<UiControl?> Backlog(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var query = $"path:{hubPath}/Todo nodeType:ACME/Project/Todo state:Active scope:subtree";

        return host.Hub.ObserveMeshSearchByProperty(
            query,
            groupByProperty: "Priority",
            groupLabelSelector: GetPriorityLabel,
            groupOrderSelector: GetPriorityOrder,
            groupExpandedSelector: key => key == "Critical" || key == "High",
            filterPredicate: n =>
                string.IsNullOrEmpty(MeshSearchExtensions.GetPropertyValue(n, "Assignee")) &&
                MeshSearchExtensions.GetPropertyValue(n, "Status") != "Completed"
        ).Select(c => c
            .WithCollapsibleSections(true)
            .WithSectionCounts(true)
            .WithGridBreakpoints(xs: 12, sm: 6, md: 4));
    }

    private static int GetPriorityOrder(string? priority) => priority switch
    {
        "Critical" => 0,
        "High" => 1,
        "Medium" => 2,
        "Low" => 3,
        _ => 4
    };

    private static string GetPriorityLabel(string? priority) => priority switch
    {
        "Critical" => "\ud83d\udea8 Critical Priority",
        "High" => "\ud83d\udd25 High Priority",
        "Medium" => "\ud83d\udfe1 Medium Priority",
        "Low" => "\ud83d\udfe2 Low Priority",
        _ => "\u2753 Unset Priority"
    };
}
