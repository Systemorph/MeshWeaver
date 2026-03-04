// <meshweaver>
// Id: ProjectLayoutAreas
// DisplayName: Project Views
// </meshweaver>

using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh.Services;

/// <summary>
/// Catalog views for Project nodes using CatalogControl with LayoutAreaControl thumbnails.
/// </summary>
public static class ProjectLayoutAreas
{
    /// <summary>
    /// Registers all Project views with the layout definition.
    /// </summary>
    public static LayoutDefinition AddProjectLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView("AllTasks", AllTasks)
            .WithView("TodosByCategory", TodosByCategory)
            .WithView("TodaysFocus", TodaysFocus)
            .WithView("Backlog", Backlog)
            .WithView("MyTasks", MyTasks);

    private static Dictionary<string, MeshNode> ApplyChanges(
        Dictionary<string, MeshNode> current, QueryResultChange<MeshNode> change)
    {
        var result = change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset
            ? new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MeshNode>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var item in change.Items)
        {
            if (change.ChangeType == QueryChangeType.Removed) result.Remove(item.Path);
            else result[item.Path] = item;
        }
        return result;
    }

    private static string? GetProp(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return null;
        if (json.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        var pascal = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
        if (json.TryGetProperty(pascal, out var pp) && pp.ValueKind == JsonValueKind.String) return pp.GetString();
        return null;
    }

    private static DateTime? GetDate(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return null;
        var name = prop;
        if (!json.TryGetProperty(name, out var p))
        {
            name = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
            if (!json.TryGetProperty(name, out p)) return null;
        }
        return p.ValueKind == JsonValueKind.String && DateTime.TryParse(p.GetString(), out var dt) ? dt : null;
    }

    private static ImmutableList<UiControl> Thumbnails(IEnumerable<MeshNode> nodes) =>
        nodes.Select(n => (UiControl)new LayoutAreaControl(n.Path, new LayoutAreaReference("Thumbnail"))
            .WithSpinnerType(SpinnerType.Dots)).ToImmutableList();

    /// <summary>Tasks grouped by status.</summary>
    [Display(GroupName = "Overview", Order = 1)]
    public static IObservable<UiControl?> AllTasks(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var statuses = host.Workspace.GetObservable<Status>()
            .Select(s => s.OrderBy(x => x.Order).ToList());
        var nodes = host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>()
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{hubPath}/Todo state:Active scope:children"))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return nodes.CombineLatest(statuses, (dict, statusList) =>
        {
            var groups = statusList.Select(status =>
            {
                var items = dict.Values
                    .Where(n => (GetProp(n, "status") ?? "Pending") == status.Id)
                    .OrderBy(n => GetDate(n, "dueDate") ?? DateTime.MaxValue)
                    .ThenBy(n => n.Name).ToList();
                return items.Any() ? new CatalogGroup
                {
                    Key = status.Id,
                    Label = status.Name,
                    Emoji = status.Emoji,
                    Order = status.Order,
                    IsExpanded = status.IsExpandedByDefault,
                    Items = Thumbnails(items),
                    TotalCount = items.Count
                } : null;
            }).Where(g => g != null).Cast<CatalogGroup>().ToImmutableList();
            return (UiControl?)new CatalogControl().WithGroups(groups);
        });
    }

    /// <summary>Tasks grouped by category.</summary>
    [Display(GroupName = "Overview", Order = 2)]
    public static IObservable<UiControl?> TodosByCategory(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var categories = host.Workspace.GetObservable<Category>()
            .Select(c => c.ToDictionary(cat => cat.Id, cat => cat));
        var nodes = host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>()
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{hubPath}/Todo state:Active scope:children"))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return nodes.CombineLatest(categories, (dict, cats) =>
        {
            var groups = dict.Values.OrderBy(n => n.Order).ThenBy(n => n.Name)
                .GroupBy(n => GetProp(n, "category") ?? "Uncategorized")
                .Select(g =>
                {
                    var cat = cats.GetValueOrDefault(g.Key) ?? Category.Uncategorized;
                    return new CatalogGroup
                    {
                        Key = g.Key,
                        Label = cat.Name,
                        Emoji = cat.Emoji,
                        Order = cat.Order,
                        IsExpanded = true,
                        Items = Thumbnails(g),
                        TotalCount = g.Count()
                    };
                }).OrderBy(g => g.Order).ToImmutableList();
            return (UiControl?)new CatalogControl().WithGroups(groups);
        });
    }

    /// <summary>Urgent tasks: overdue, due today, in progress.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> TodaysFocus(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var statuses = host.Workspace.GetObservable<Status>()
            .Select(s => s.ToDictionary(x => x.Id, x => x));
        var priorities = host.Workspace.GetObservable<Priority>()
            .Select(p => p.ToDictionary(x => x.Id, x => x));
        var nodes = host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>()
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{hubPath}/Todo state:Active scope:children"))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return nodes.CombineLatest(statuses, priorities, (dict, sts, pris) =>
        {
            var completedId = sts.Values.FirstOrDefault(s => s.Id == "Completed")?.Id ?? "Completed";
            var inProgressId = sts.Values.FirstOrDefault(s => s.Id == "InProgress")?.Id ?? "InProgress";
            var inProgress = sts.GetValueOrDefault("InProgress");
            var completed = sts.GetValueOrDefault("Completed");
            var critical = pris.GetValueOrDefault("Critical");

            var now = DateTime.Now.Date;
            var active = dict.Values.Where(n => GetProp(n, "status") != completedId).ToList();
            var groups = new List<CatalogGroup>();

            var overdue = active.Where(n => GetDate(n, "dueDate")?.Date < now)
                .OrderBy(n => GetDate(n, "dueDate")).ToList();
            if (overdue.Any())
                groups.Add(new CatalogGroup
                {
                    Key = "overdue",
                    Label = "Overdue",
                    Emoji = critical?.Emoji ?? "\ud83d\udea8",
                    Order = 0,
                    IsExpanded = true,
                    Items = Thumbnails(overdue),
                    TotalCount = overdue.Count
                });

            var today = active.Where(n => GetDate(n, "dueDate")?.Date == now)
                .OrderBy(n => pris.GetValueOrDefault(GetProp(n, "priority") ?? "")?.Order ?? 99).ToList();
            if (today.Any())
                groups.Add(new CatalogGroup
                {
                    Key = "today",
                    Label = "Due Today",
                    Emoji = "\u23f0",
                    Order = 1,
                    IsExpanded = true,
                    Items = Thumbnails(today),
                    TotalCount = today.Count
                });

            var inProg = active.Where(n => GetProp(n, "status") == inProgressId &&
                    GetDate(n, "dueDate")?.Date != now &&
                    (GetDate(n, "dueDate")?.Date >= now || !GetDate(n, "dueDate").HasValue))
                .OrderBy(n => GetDate(n, "dueDate") ?? DateTime.MaxValue).ToList();
            if (inProg.Any())
                groups.Add(new CatalogGroup
                {
                    Key = "inProgress",
                    Label = inProgress?.Name ?? "In Progress",
                    Emoji = inProgress?.Emoji ?? "\ud83d\udd04",
                    Order = 2,
                    IsExpanded = true,
                    Items = Thumbnails(inProg),
                    TotalCount = inProg.Count
                });

            return groups.Any()
                ? (UiControl?)new CatalogControl().WithGroups(groups.ToImmutableList())
                : Controls.Markdown($"{completed?.Emoji ?? "\u2705"} **All caught up!** No urgent tasks.");
        });
    }

    /// <summary>Unassigned tasks by priority.</summary>
    [Display(GroupName = "Planning", Order = 1)]
    public static IObservable<UiControl?> Backlog(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var statuses = host.Workspace.GetObservable<Status>()
            .Select(s => s.ToDictionary(x => x.Id, x => x));
        var priorities = host.Workspace.GetObservable<Priority>()
            .Select(p => p.ToDictionary(x => x.Id, x => x));
        var nodes = host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>()
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{hubPath}/Todo state:Active scope:children"))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return nodes.CombineLatest(statuses, priorities, (dict, sts, pris) =>
        {
            var completedId = sts.Values.FirstOrDefault(s => s.Id == "Completed")?.Id ?? "Completed";
            var defaultPriority = pris.Values.FirstOrDefault(p => p.Id == "Unset") ?? new Priority { Id = "Unset", Name = "Unset", Order = 99 };

            var backlog = dict.Values
                .Where(n => string.IsNullOrEmpty(GetProp(n, "assignee")) && GetProp(n, "status") != completedId)
                .ToList();
            if (!backlog.Any())
                return (UiControl?)Controls.Markdown("*No unassigned tasks.*");

            var groups = backlog.GroupBy(n => GetProp(n, "priority") ?? defaultPriority.Id)
                .Select(g =>
                {
                    var priority = pris.GetValueOrDefault(g.Key) ?? defaultPriority;
                    return new CatalogGroup
                    {
                        Key = g.Key,
                        Label = priority.Name,
                        Emoji = priority.Emoji,
                        Order = priority.Order,
                        IsExpanded = priority.IsExpandedByDefault,
                        Items = Thumbnails(g.OrderBy(n => n.Order).ThenBy(n => n.Name)),
                        TotalCount = g.Count()
                    };
                }).OrderBy(g => g.Order).ToImmutableList();
            return (UiControl?)new CatalogControl().WithGroups(groups);
        });
    }

    /// <summary>Current user's tasks by urgency.</summary>
    [Display(GroupName = "My Work", Order = 0)]
    public static IObservable<UiControl?> MyTasks(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var user = host.Hub.ServiceProvider.GetService<AccessService>()?.Context?.Name ?? "Guest";
        var statuses = host.Workspace.GetObservable<Status>()
            .Select(s => s.ToDictionary(x => x.Id, x => x));
        var priorities = host.Workspace.GetObservable<Priority>()
            .Select(p => p.ToDictionary(x => x.Id, x => x));
        var nodes = host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>()
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{hubPath}/Todo state:Active scope:children"))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return nodes.CombineLatest(statuses, priorities, (dict, sts, pris) =>
        {
            var completedId = sts.Values.FirstOrDefault(s => s.Id == "Completed")?.Id ?? "Completed";
            var critical = pris.GetValueOrDefault("Critical");

            var now = DateTime.Now.Date;
            var my = dict.Values
                .Where(n => GetProp(n, "assignee") == user && GetProp(n, "status") != completedId)
                .ToList();
            if (!my.Any())
                return (UiControl?)Controls.Markdown($"*No active tasks for {user}.*");

            var groups = new List<CatalogGroup>();

            var urgent = my.Where(n => GetDate(n, "dueDate")?.Date <= now)
                .OrderBy(n => GetDate(n, "dueDate")).ToList();
            if (urgent.Any())
                groups.Add(new CatalogGroup
                {
                    Key = "urgent",
                    Label = "Urgent",
                    Emoji = critical?.Emoji ?? "\ud83d\udea8",
                    Order = 0,
                    IsExpanded = true,
                    Items = Thumbnails(urgent),
                    TotalCount = urgent.Count
                });

            var tomorrow = my.Where(n => GetDate(n, "dueDate")?.Date == now.AddDays(1)).ToList();
            if (tomorrow.Any())
                groups.Add(new CatalogGroup
                {
                    Key = "tomorrow",
                    Label = "Tomorrow",
                    Emoji = "\ud83d\udcc5",
                    Order = 1,
                    IsExpanded = true,
                    Items = Thumbnails(tomorrow),
                    TotalCount = tomorrow.Count
                });

            var upcoming = my.Where(n => !GetDate(n, "dueDate").HasValue || GetDate(n, "dueDate")?.Date > now.AddDays(1))
                .OrderBy(n => GetDate(n, "dueDate") ?? DateTime.MaxValue).ToList();
            if (upcoming.Any())
                groups.Add(new CatalogGroup
                {
                    Key = "upcoming",
                    Label = "Upcoming",
                    Emoji = "\ud83d\uddd3\ufe0f",
                    Order = 2,
                    IsExpanded = true,
                    Items = Thumbnails(upcoming),
                    TotalCount = upcoming.Count
                });

            return (UiControl?)new CatalogControl().WithGroups(groups.ToImmutableList());
        });
    }
}
