// <meshweaver>
// Id: ProjectViews
// DisplayName: Project Views
// </meshweaver>

/// <summary>
/// Minimal Todo projection for project views.
/// Contains only properties needed for display, with Path for navigation.
/// </summary>
internal record TodoItem(
    string Path,
    string Id,
    string Title,
    string Status,  // string instead of enum to avoid dependency on Todo module
    string? Priority,
    string? Category,
    string? Assignee,
    DateTime? DueDate,
    DateTime CreatedAt
);

/// <summary>
/// Custom views for Project nodes showing aggregated Todo item statistics and management.
/// </summary>
public static class ProjectViews
{
    #region View Configuration

    /// <summary>
    /// Configuration for rendering a todo view with grouping logic.
    /// Supports both JsonPointer-based grouping and custom grouping functions.
    /// </summary>
    private record ViewConfig(
        string Title,
        string Icon,
        string? GroupByPointer = null,  // e.g., "/category", "/assignee", "/status"
        Func<string?, string>? GroupKeyFormatter = null,
        Func<string?, int>? GroupKeyOrder = null,
        Func<string?, bool>? GroupOpenPredicate = null,
        Func<List<TodoItem>, string>? Summary = null,
        // Legacy support for complex grouping
        Func<List<TodoItem>, IEnumerable<(string Section, List<TodoItem> Items, bool Open)>>? CustomGroupItems = null
    );

    private static readonly string[] StatusOrder = { "Pending", "InProgress", "InReview", "Blocked", "Completed" };

    private static readonly ViewConfig ByStatusConfig = new(
        Title: "All Tasks",
        Icon: "\ud83d\udcdd",
        GroupByPointer: "/status",
        GroupKeyFormatter: key => $"{GetStatusIcon(key)} {key ?? "Unknown"}",
        GroupKeyOrder: key => Array.IndexOf(StatusOrder, key) is var idx && idx >= 0 ? idx : 99,
        GroupOpenPredicate: key => key != "Completed"
    );

    private static readonly ViewConfig ByCategoryConfig = new(
        Title: "Tasks by Category",
        Icon: "\ud83d\udcc2",
        GroupByPointer: "/category",
        GroupKeyFormatter: key => $"\ud83d\udcc1 {key ?? "Uncategorized"}"
    );

    private static readonly ViewConfig BacklogByPriorityConfig = new(
        Title: "Backlog - Unassigned Tasks",
        Icon: "\ud83d\udccb",
        GroupByPointer: "/priority",
        GroupKeyFormatter: key => GetPriorityLabel(key),
        GroupKeyOrder: key => GetPriorityOrder(key)
    );

    #endregion

    #region Observable Query Helpers

    /// <summary>
    /// Creates an observable stream of TodoItems from the mesh query.
    /// Uses Scan to maintain cumulative state from incremental changes.
    /// </summary>
    private static IObservable<List<TodoItem>> ObserveTodos(
        LayoutAreaHost host,
        string basePath,
        MeshNodeState? stateFilter = MeshNodeState.Active)
    {
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery == null)
            return Observable.Return(new List<TodoItem>());

        var stateClause = stateFilter.HasValue ? $"state:{stateFilter}" : "";
        var query = $"path:{basePath}/Todo nodeType:ACME/Project/Todo {stateClause} scope:subtree";

        return meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase),
                  (current, change) => ApplyChanges(current, change))
            .Select(dict => dict.Values
                .Select(ProjectToTodoItem)
                .Where(t => t != null)
                .Cast<TodoItem>()
                .ToList());
    }

    /// <summary>
    /// Applies query result changes to the cumulative node dictionary.
    /// </summary>
    private static Dictionary<string, MeshNode> ApplyChanges(
        Dictionary<string, MeshNode> current,
        QueryResultChange<MeshNode> change)
    {
        var result = change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset
            ? new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MeshNode>(current, StringComparer.OrdinalIgnoreCase);

        foreach (var item in change.Items)
        {
            if (change.ChangeType == QueryChangeType.Removed)
                result.Remove(item.Path);
            else
                result[item.Path] = item;
        }
        return result;
    }

    /// <summary>
    /// Projects a MeshNode to a minimal TodoItem for display.
    /// Uses JsonPointer-based property extraction to avoid dependency on Todo types.
    /// </summary>
    private static TodoItem? ProjectToTodoItem(MeshNode node)
    {
        if (node.Content == null) return null;

        var dueDate = GetPointerDateTime(node.Content, "/DueDate");
        if (!dueDate.HasValue)
        {
            var offset = GetPointerInt(node.Content, "/DueDateOffsetDays");
            if (offset.HasValue)
                dueDate = DateTime.UtcNow.Date.AddDays(offset.Value);
        }

        return new TodoItem(
            Path: node.Path,
            Id: GetPointerString(node.Content, "/Id") ?? "",
            Title: GetPointerString(node.Content, "/Title") ?? "",
            Status: GetPointerString(node.Content, "/Status") ?? "Pending",
            Priority: GetPointerString(node.Content, "/Priority"),
            Category: GetPointerString(node.Content, "/Category"),
            Assignee: GetPointerString(node.Content, "/Assignee"),
            DueDate: dueDate,
            CreatedAt: GetPointerDateTime(node.Content, "/CreatedAt") ?? DateTime.UtcNow
        );
    }

    private static string ToCamelCase(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

    #endregion

    #region JsonPointer Helpers

    private static System.Text.Json.JsonElement? EvaluatePointer(object? content, string pointerPath)
    {
        if (content == null) return null;
        var pointer = Json.Pointer.JsonPointer.Parse(pointerPath);
        if (content is System.Text.Json.JsonElement jsonElement)
            return pointer.Evaluate(jsonElement);
        var serialized = System.Text.Json.JsonSerializer.SerializeToElement(content);
        return pointer.Evaluate(serialized);
    }

    private static string? GetPointerString(object? content, string pointerPath)
    {
        var result = EvaluatePointer(content, pointerPath)
                  ?? EvaluatePointer(content, ToCamelCasePointer(pointerPath));
        return result?.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => result.Value.GetString(),
            System.Text.Json.JsonValueKind.Null or null => null,
            _ => result.Value.ToString()
        };
    }

    private static DateTime? GetPointerDateTime(object? content, string pointerPath)
    {
        var str = GetPointerString(content, pointerPath);
        return DateTime.TryParse(str, out var dt) ? dt : null;
    }

    private static int? GetPointerInt(object? content, string pointerPath)
    {
        var result = EvaluatePointer(content, pointerPath)
                  ?? EvaluatePointer(content, ToCamelCasePointer(pointerPath));
        return result?.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => result.Value.GetInt32(),
            System.Text.Json.JsonValueKind.String when int.TryParse(result.Value.GetString(), out var i) => i,
            _ => null
        };
    }

    private static string ToCamelCasePointer(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer.Length < 2) return pointer;
        var idx = pointer.IndexOf('/');
        if (idx < 0 || idx + 1 >= pointer.Length) return pointer;
        return pointer[..(idx + 1)] + char.ToLowerInvariant(pointer[idx + 1]) + pointer[(idx + 2)..];
    }

    #endregion

    #region Pointer-Based Grouping

    private static IEnumerable<(string Section, List<TodoItem> Items, bool Open)> GroupByPointer(
        List<TodoItem> todos,
        ViewConfig config)
    {
        if (string.IsNullOrEmpty(config.GroupByPointer))
        {
            yield return (config.Title, todos, true);
            yield break;
        }

        var grouped = todos
            .GroupBy(t => GetTodoProperty(t, config.GroupByPointer))
            .OrderBy(g => config.GroupKeyOrder?.Invoke(g.Key) ?? 0)
            .ThenByDescending(g => g.Count());

        foreach (var group in grouped)
        {
            var label = config.GroupKeyFormatter?.Invoke(group.Key) ?? group.Key ?? "Unknown";
            var isOpen = config.GroupOpenPredicate?.Invoke(group.Key) ?? true;
            yield return ($"{label} ({group.Count()})", group.ToList(), isOpen);
        }
    }

    private static string? GetTodoProperty(TodoItem item, string pointerPath) => pointerPath switch
    {
        "/status" or "/Status" => item.Status,
        "/category" or "/Category" => item.Category,
        "/assignee" or "/Assignee" => item.Assignee,
        "/priority" or "/Priority" => item.Priority,
        _ => null
    };

    #endregion

    #region Reactive View Rendering

    /// <summary>
    /// Renders a todo view using the reactive pattern with ViewConfig.
    /// </summary>
    private static IObservable<UiControl?> RenderTodoView(
        LayoutAreaHost host,
        ViewConfig config,
        MeshNodeState? stateFilter = MeshNodeState.Active)
    {
        var hubPath = host.Hub.Address.ToString();
        return ObserveTodos(host, hubPath, stateFilter)
            .Select(todos => BuildViewFromConfig(todos, host, hubPath, config));
    }

    /// <summary>
    /// Builds a view from TodoItems using the ViewConfig.
    /// </summary>
    private static UiControl BuildViewFromConfig(
        List<TodoItem> todos,
        LayoutAreaHost host,
        string hubPath,
        ViewConfig config)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header row with title
        mainGrid = mainGrid
            .WithView(Controls.H4($"{config.Icon} {config.Title}")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));

        if (!todos.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("*No tasks found.*"),
                skin => skin.WithXs(12));
        }

        // Optional summary line
        if (config.Summary != null)
        {
            mainGrid = mainGrid.WithView(
                Controls.Markdown(config.Summary(todos))
                    .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));
        }

        // Determine grouping strategy
        var groupedSections = config.CustomGroupItems != null
            ? config.CustomGroupItems(todos)
            : GroupByPointer(todos, config);

        foreach (var (section, items, open) in groupedSections)
        {
            if (!items.Any()) continue;

            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems(section, items, open),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// Builds a collapsible section from TodoItems using LayoutAreaControl.
    /// Uses a responsive grid layout: 3 columns on large, 2 on medium, 1 on small screens.
    /// </summary>
    private static UiControl BuildCollapsibleSectionFromItems(string title, List<TodoItem> items, bool defaultOpen)
    {
        var sectionStack = Controls.Stack
            .WithStyle(style => style.WithMarginBottom("32px"))
            .WithView(BuildSectionHeader(title));

        var itemsGrid = CreateItemsGrid();
        foreach (var item in items)
        {
            itemsGrid = itemsGrid.WithView(
                Controls.LayoutArea(new Address(item.Path), "Thumbnail").WithSpinnerType(SpinnerType.Dots),
                skin => skin.WithXs(12).WithSm(6).WithMd(4));
        }

        return sectionStack.WithView(itemsGrid);
    }

    #endregion

    #region Public View Methods

    /// <summary>
    /// Summary view showing aggregated statistics for all child Todo items.
    /// Uses reactive ObserveQuery for live updates.
    /// </summary>
    [Display(GroupName = "Overview", Order = 1)]
    public static IObservable<UiControl?> Summary(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return ObserveTodos(host, hubPath)
            .Select(todos => BuildSummaryView(todos, hubPath));
    }

    private static UiControl BuildSummaryView(List<TodoItem> todos, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83d\udcca Project Dashboard")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));

        if (!todos.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("*No tasks found. Add your first task to get started!*"),
                skin => skin.WithXs(12));
        }

        // Overall statistics
        var totalCount = todos.Count;
        var completedCount = todos.Count(t => t.Status == "Completed");
        var completionRate = totalCount > 0 ? (completedCount * 100.0 / totalCount) : 0;

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"**Total Tasks:** {totalCount} | **Completed:** {completedCount} ({completionRate:F0}%)"),
                skin => skin.WithXs(12));

        // Status breakdown
        mainGrid = mainGrid
            .WithView(Controls.H5("\ud83d\udcc8 Status Overview")
                .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("8px")),
                skin => skin.WithXs(12));

        var statusOrder = new[] { "Pending", "InProgress", "InReview", "Blocked", "Completed" };
        var statusGroups = todos.GroupBy(t => t.Status).OrderBy(g => Array.IndexOf(statusOrder, g.Key));
        foreach (var group in statusGroups)
        {
            var icon = GetStatusIcon(group.Key);
            var percentage = (group.Count() * 100.0 / totalCount).ToString("F1");
            mainGrid = mainGrid
                .WithView(Controls.Markdown($"{icon} **{group.Key}**: {group.Count()} ({percentage}%)")
                    .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px")),
                    skin => skin.WithXs(12));
        }

        // Visual progress bars
        var inProgressCount = todos.Count(t => t.Status == "InProgress");
        var blockedCount = todos.Count(t => t.Status == "Blocked");
        var inProgressRate = totalCount > 0 ? (inProgressCount * 100.0 / totalCount) : 0;
        var blockedRate = totalCount > 0 ? (blockedCount * 100.0 / totalCount) : 0;

        mainGrid = mainGrid
            .WithView(Controls.Html($@"
                <div style=""margin: 16px 0; padding: 12px; background: var(--neutral-layer-2); border-radius: 8px;"">
                    {BuildProgressBar("✅", "Completion", completionRate, "#28a745")}
                    {BuildProgressBar("🔄", "In Progress", inProgressRate, "#007bff")}
                    {BuildProgressBar("🚫", "Blocked", blockedRate, "#dc3545").Replace("margin-bottom: 12px;", "")}
                </div>"),
                skin => skin.WithXs(12));

        // Due date analysis
        var now = DateTime.Now.Date;
        var overdue = todos.Count(t => t.DueDate?.Date < now && t.Status != "Completed");
        var dueToday = todos.Count(t => t.DueDate?.Date == now && t.Status != "Completed");
        var dueSoon = todos.Count(t => t.DueDate?.Date > now && t.DueDate?.Date <= now.AddDays(7) && t.Status != "Completed");

        mainGrid = mainGrid
            .WithView(Controls.H5("\u23f0 Due Date Insights")
                .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("8px")),
                skin => skin.WithXs(12))
            .WithView(Controls.Markdown($"\ud83d\udea8 **Overdue**: {overdue}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px").WithColor(overdue > 0 ? "#dc3545" : "inherit")),
                skin => skin.WithXs(12))
            .WithView(Controls.Markdown($"\u23f0 **Due Today**: {dueToday}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px").WithColor(dueToday > 0 ? "#fd7e14" : "inherit")),
                skin => skin.WithXs(12))
            .WithView(Controls.Markdown($"\ud83d\udcc5 **Due This Week**: {dueSoon}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px")),
                skin => skin.WithXs(12));

        // Priority breakdown
        var criticalCount = todos.Count(t => t.Priority == "Critical" && t.Status != "Completed");
        var highCount = todos.Count(t => t.Priority == "High" && t.Status != "Completed");

        if (criticalCount > 0 || highCount > 0)
        {
            mainGrid = mainGrid
                .WithView(Controls.H5("\u26a0\ufe0f Priority Items")
                    .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("8px")),
                    skin => skin.WithXs(12));

            if (criticalCount > 0)
                mainGrid = mainGrid.WithView(Controls.Markdown($"\ud83d\udea8 **Critical**: {criticalCount}")
                    .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px").WithColor("#dc3545")),
                    skin => skin.WithXs(12));

            if (highCount > 0)
                mainGrid = mainGrid.WithView(Controls.Markdown($"\ud83d\udd25 **High Priority**: {highCount}")
                    .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px").WithColor("#fd7e14")),
                    skin => skin.WithXs(12));
        }

        // Team workload
        var assignees = todos.Where(t => !string.IsNullOrEmpty(t.Assignee) && t.Status != "Completed")
            .GroupBy(t => t.Assignee!)
            .OrderByDescending(g => g.Count())
            .Take(5);

        var unassigned = todos.Count(t => string.IsNullOrEmpty(t.Assignee) && t.Status != "Completed");

        mainGrid = mainGrid
            .WithView(Controls.H5("\ud83d\udc65 Team Workload")
                .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("8px")),
                skin => skin.WithXs(12));

        foreach (var assignee in assignees)
        {
            mainGrid = mainGrid.WithView(Controls.Markdown($"\ud83d\udc64 **{assignee.Key}**: {assignee.Count()} tasks")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px")),
                skin => skin.WithXs(12));
        }

        if (unassigned > 0)
        {
            mainGrid = mainGrid.WithView(Controls.Markdown($"\ud83d\udccb **Unassigned**: {unassigned} tasks")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px").WithColor("#ffc107")),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// AllTasks view listing all child Todo items with status grouping and actions.
    /// Uses reactive ObserveQuery for live updates of both active and deleted items.
    /// </summary>
    [Display(GroupName = "Overview", Order = 2)]
    public static IObservable<UiControl?> AllTasks(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Combine active and deleted todo streams for reactive updates
        var activeTodos = ObserveTodos(host, hubPath, MeshNodeState.Active);
        var deletedTodos = ObserveTodos(host, hubPath, MeshNodeState.Deleted);

        return activeTodos.CombineLatest(deletedTodos, (active, deleted) =>
            BuildAllTasksView(active, deleted, host, hubPath));
    }

    private static UiControl BuildAllTasksView(List<TodoItem> todos, List<TodoItem> deletedTodos, LayoutAreaHost host, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));
        mainGrid = AddPageHeader(mainGrid, "\ud83d\udcdd", "All Tasks");

        if (!todos.Any() && !deletedTodos.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("*No tasks found.*"),
                skin => skin.WithXs(12));
        }

        // Group by status
        var allStatusOrder = new[] { "Pending", "InProgress", "InReview", "Blocked", "Completed" };
        foreach (var status in allStatusOrder)
        {
            var statusTodos = todos.Where(t => t.Status == status)
                .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
                .ThenBy(t => t.CreatedAt)
                .ToList();

            if (!statusTodos.Any()) continue;

            var icon = GetStatusIcon(status);
            var isExpanded = status != "Completed";

            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems($"{icon} {status} ({statusTodos.Count})", statusTodos, isExpanded),
                skin => skin.WithXs(12));
        }

        // Deleted section at the bottom
        if (deletedTodos.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildDeletedSection(deletedTodos, hubPath, host),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    private static UiControl BuildDeletedSection(List<TodoItem> deletedTodos, string hubPath, LayoutAreaHost host)
    {
        var sectionStack = Controls.Stack
            .WithStyle(style => style.WithMarginBottom("32px").WithMarginTop("8px"))
            .WithView(BuildSectionHeader($"🗑️ Deleted ({deletedTodos.Count})", 0.8));

        var itemsGrid = CreateItemsGrid();
        foreach (var todo in deletedTodos)
        {
            itemsGrid = itemsGrid.WithView(
                BuildDeletedTodoListItem(todo, host),
                skin => skin.WithXs(12).WithSm(6).WithMd(4));
        }

        return sectionStack.WithView(itemsGrid);
    }

    /// <summary>
    /// TodosByCategory view grouping items by category.
    /// Uses reactive ObserveQuery for live updates.
    /// </summary>
    [Display(GroupName = "Overview", Order = 3)]
    public static IObservable<UiControl?> TodosByCategory(LayoutAreaHost host, RenderingContext _)
        => RenderTodoView(host, ByCategoryConfig);

    /// <summary>
    /// Planning view for workload management and task assignment.
    /// Uses reactive ObserveQuery for live updates.
    /// </summary>
    [Display(GroupName = "Planning")]
    public static IObservable<UiControl?> Planning(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return ObserveTodos(host, hubPath)
            .Select(todos => BuildPlanningView(todos, host, hubPath));
    }

    private static UiControl BuildPlanningView(List<TodoItem> todos, LayoutAreaHost host, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));
        mainGrid = AddPageHeader(mainGrid, "\ud83c\udfaf", "Planning & Assignment");

        if (!todos.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("*No tasks found. Create some tasks to start planning!*"),
                skin => skin.WithXs(12));
        }

        var activeTodos = todos.Where(t => t.Status != "Completed").ToList();

        // Team workload section
        var teamWorkload = activeTodos
            .Where(t => !string.IsNullOrEmpty(t.Assignee))
            .GroupBy(t => t.Assignee!)
            .OrderByDescending(g => g.Count())
            .ToList();

        mainGrid = mainGrid
            .WithView(Controls.H5($"\ud83d\udc65 Team Workload ({teamWorkload.Sum(g => g.Count())} assigned)")
                .WithStyle(style => style.WithMarginTop("8px").WithMarginBottom("12px")),
                skin => skin.WithXs(12));

        foreach (var assignee in teamWorkload.Take(10))
        {
            var taskCount = assignee.Count();
            var overdueCount = assignee.Count(t => t.DueDate?.Date < DateTime.Now.Date);
            var workloadIndicator = taskCount <= 2 ? "\ud83d\udfe2" : taskCount <= 4 ? "\ud83d\udfe1" : "\ud83d\udd34";
            var overdueText = overdueCount > 0 ? $" (\ud83d\udea8 {overdueCount} overdue)" : "";

            mainGrid = mainGrid.WithView(
                Controls.Markdown($"{workloadIndicator} **{assignee.Key}**: {taskCount} tasks{overdueText}")
                    .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("6px")),
                skin => skin.WithXs(12));
        }

        // Unassigned tasks section
        var unassignedTasks = activeTodos
            .Where(t => string.IsNullOrEmpty(t.Assignee))
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ToList();

        if (unassignedTasks.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.H5($"\ud83d\udccb Unassigned Tasks ({unassignedTasks.Count})")
                    .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("12px").WithColor("#ffc107")),
                    skin => skin.WithXs(12));

            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems("View unassigned tasks", unassignedTasks, true),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// MyTasks view showing current user's active tasks.
    /// Uses reactive ObserveQuery for live updates.
    /// </summary>
    [Display(GroupName = "My Work")]
    public static IObservable<UiControl?> MyTasks(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return ObserveTodos(host, hubPath)
            .Select(todos => BuildMyTasksView(todos, hubPath, host));
    }

    private static UiControl BuildMyTasksView(List<TodoItem> todos, string hubPath, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));
        mainGrid = AddPageHeader(mainGrid, "\ud83d\udfe2", "My Tasks");

        // Get current user from AccessService
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? accessService?.Context?.ObjectId ?? "Guest";
        var myTasks = todos
            .Where(t => t.Assignee == currentUser && t.Status != "Completed")
            .ToList();

        if (!myTasks.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown($"*No active tasks assigned to {currentUser}. Great job!*"),
                skin => skin.WithXs(12));
        }

        var now = DateTime.Now.Date;

        // Urgent tasks (overdue + due today)
        var urgentTasks = myTasks
            .Where(t => t.DueDate?.Date <= now)
            .OrderBy(t => t.DueDate)
            .ToList();

        // Tomorrow
        var tomorrowTasks = myTasks
            .Where(t => t.DueDate?.Date == now.AddDays(1))
            .ToList();

        // Upcoming
        var upcomingTasks = myTasks
            .Where(t => !t.DueDate.HasValue || t.DueDate?.Date > now.AddDays(1))
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ToList();

        // Summary
        var inProgressCount = myTasks.Count(t => t.Status == "InProgress");
        var overdueCount = urgentTasks.Count(t => t.DueDate?.Date < now);

        mainGrid = mainGrid.WithView(
            Controls.Markdown($"**Active**: {myTasks.Count} | **In Progress**: {inProgressCount} | **Overdue**: {overdueCount}")
                .WithStyle(style => style.WithMarginBottom("16px")),
            skin => skin.WithXs(12));

        if (urgentTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems($"\ud83d\udea8 Urgent ({urgentTasks.Count})", urgentTasks, true),
                skin => skin.WithXs(12));
        }

        if (tomorrowTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems($"\ud83d\udcc5 Tomorrow ({tomorrowTasks.Count})", tomorrowTasks, true),
                skin => skin.WithXs(12));
        }

        if (upcomingTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems($"\ud83d\uddd3\ufe0f Upcoming ({upcomingTasks.Count})", upcomingTasks, true),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// Backlog view showing unassigned tasks grouped by priority.
    /// Uses reactive ObserveQuery for live updates with pre-filter for unassigned tasks.
    /// </summary>
    [Display(GroupName = "Planning")]
    public static IObservable<UiControl?> Backlog(LayoutAreaHost host, RenderingContext _)
    {
        // Use BacklogByPriorityConfig with CustomGroupItems to pre-filter unassigned tasks
        var backlogConfig = BacklogByPriorityConfig with
        {
            Summary = todos =>
            {
                var count = todos.Count(t => string.IsNullOrEmpty(t.Assignee) && t.Status != "Completed");
                return count > 0 ? $"**{count} tasks** waiting to be assigned" : "*No unassigned tasks. All tasks have been assigned!*";
            },
            CustomGroupItems = todos =>
            {
                var backlogTasks = todos
                    .Where(t => string.IsNullOrEmpty(t.Assignee) && t.Status != "Completed")
                    .ToList();

                if (!backlogTasks.Any())
                    return Enumerable.Empty<(string, List<TodoItem>, bool)>();

                return backlogTasks
                    .GroupBy(t => t.Priority switch
                    {
                        "Critical" => "Critical",
                        "High" => "High",
                        _ => "Normal"
                    })
                    .OrderBy(g => g.Key switch { "Critical" => 0, "High" => 1, _ => 2 })
                    .Select(g => (GetPriorityLabel(g.Key == "Normal" ? "Medium" : g.Key) + $" ({g.Count()})",
                                  g.OrderBy(t => t.DueDate ?? DateTime.MaxValue).ToList(),
                                  g.Key != "Normal"));
            }
        };
        return RenderTodoView(host, backlogConfig);
    }

    private static UiControl BuildDeletedTodoListItem(TodoItem todo, LayoutAreaHost host)
    {
        var priorityBadge = GetPriorityBadge(todo.Priority);
        var todoPath = todo.Path;

        // Card layout for deleted item
        var card = Controls.Stack
            .WithStyle(style => style
                .WithPadding("12px")
                .WithBorder("1px solid var(--neutral-stroke-rest)")
                .WithBorderRadius("8px")
                .WithBackgroundColor("var(--neutral-layer-2)")
                .WithHeight("100%"));

        // Header: icon + title + priority
        card = card.WithView(Controls.Html($@"
            <div style=""display: flex; align-items: center; gap: 8px; margin-bottom: 8px; opacity: 0.8;"">
                <span style=""font-size: 16px;"">🗑️</span>
                <a href=""/{todoPath}"" style=""flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; text-decoration: line-through; color: inherit; font-weight: 600;"">
                    {System.Web.HttpUtility.HtmlEncode(todo.Title)}
                </a>
                {priorityBadge}
            </div>"));

        // Action buttons row
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(style => style.WithGap("8px"));

        // Restore button
        buttonRow = buttonRow.WithView(
            Controls.Button("↩️ Restore")
                .WithAppearance(Appearance.Accent)
                .WithStyle(style => style.WithPadding("4px 8px"))
                .WithClickAction(actx => RestoreTodo(actx.Host, todoPath)));

        // Permanent delete button
        buttonRow = buttonRow.WithView(
            Controls.Button("🔥")
                .WithLabel("Delete Forever")
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => style.WithPadding("4px 8px").WithColor("#dc3545"))
                .WithClickAction(actx =>
                {
                    ShowPermanentDeleteConfirmationDialog(actx.Host, todo, todoPath);
                    return System.Threading.Tasks.Task.CompletedTask;
                }));

        card = card.WithView(buttonRow);

        return card;
    }

    /// <summary>
    /// TodaysFocus view showing urgent items (overdue + due today + in-progress).
    /// Uses reactive ObserveQuery for live updates.
    /// </summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> TodaysFocus(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return ObserveTodos(host, hubPath)
            .Select(todos => BuildTodaysFocusView(todos, hubPath, host));
    }

    private static UiControl BuildTodaysFocusView(List<TodoItem> todos, string hubPath, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));
        mainGrid = AddPageHeader(mainGrid, "\ud83c\udfaf", "Today's Focus");

        var now = DateTime.Now.Date;
        var activeTodos = todos.Where(t => t.Status != "Completed").ToList();

        // Overdue items
        var overdue = activeTodos
            .Where(t => t.DueDate?.Date < now)
            .OrderBy(t => t.DueDate)
            .ToList();

        // Due today
        var dueToday = activeTodos
            .Where(t => t.DueDate?.Date == now)
            .OrderBy(t => GetPriorityOrder(t.Priority))
            .ToList();

        // In progress (regardless of due date)
        var inProgress = activeTodos
            .Where(t => t.Status == "InProgress" && t.DueDate?.Date != now && t.DueDate?.Date >= now)
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ToList();

        if (!overdue.Any() && !dueToday.Any() && !inProgress.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("\u2705 **All caught up!** No urgent tasks for today.")
                    .WithStyle(style => style.WithPadding("20px").WithBackgroundColor("var(--accent-fill-rest)").WithBorderRadius("8px")),
                skin => skin.WithXs(12));
        }

        // Summary header
        mainGrid = mainGrid.WithView(
            Controls.Markdown($"\ud83d\udea8 **Overdue**: {overdue.Count} | \u23f0 **Due Today**: {dueToday.Count} | \ud83d\udd04 **In Progress**: {inProgress.Count}")
                .WithStyle(style => style.WithMarginBottom("16px")),
            skin => skin.WithXs(12));

        if (overdue.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems($"\ud83d\udea8 Overdue ({overdue.Count})", overdue, true),
                skin => skin.WithXs(12));
        }

        if (dueToday.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems($"\u23f0 Due Today ({dueToday.Count})", dueToday, true),
                skin => skin.WithXs(12));
        }

        if (inProgress.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionFromItems($"\ud83d\udd04 In Progress ({inProgress.Count})", inProgress, true),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    #endregion

    #region Helper Methods

    private static string GetStatusIcon(string? status) => status switch
    {
        "Pending" => "\u23f3",
        "InProgress" => "\ud83d\udd04",
        "InReview" => "\ud83d\udc41\ufe0f",
        "Completed" => "\u2705",
        "Blocked" => "\ud83d\udeab",
        _ => "\u2753"
    };

    private static string GetPriorityBadge(string? priority) => priority switch
    {
        "Critical" => "<span style=\"background: #dc3545; color: white; padding: 1px 4px; border-radius: 3px; font-size: 10px;\">CRITICAL</span>",
        "High" => "<span style=\"background: #fd7e14; color: white; padding: 1px 4px; border-radius: 3px; font-size: 10px;\">HIGH</span>",
        "Medium" => "<span style=\"background: #17a2b8; color: white; padding: 1px 4px; border-radius: 3px; font-size: 10px;\">MEDIUM</span>",
        "Low" => "<span style=\"background: #6c757d; color: white; padding: 1px 4px; border-radius: 3px; font-size: 10px;\">LOW</span>",
        _ => ""
    };

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
        "Medium" or "Low" => "\u2796 Normal Priority",
        _ => "\u2753 Unknown Priority"
    };

    private static LayoutGridControl AddPageHeader(LayoutGridControl grid, string icon, string title)
    {
        return grid
            .WithView(Controls.H4($"{icon} {title}")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));
    }

    private static UiControl BuildSectionHeader(string title, double opacity = 1.0)
    {
        var opacityStyle = opacity < 1.0 ? $" opacity: {opacity};" : "";
        return Controls.Html($@"
            <div style=""padding: 10px 14px; background: var(--neutral-layer-2); border-radius: 6px; font-weight: 600; font-size: 1.1rem; margin-bottom: 8px;{opacityStyle}"">
                {title}
            </div>");
    }

    private static LayoutGridControl CreateItemsGrid()
    {
        return Controls.LayoutGrid
            .WithSkin(skin => skin.WithSpacing(1))
            .WithStyle(style => style.WithWidth("100%"));
    }

    private static string BuildProgressBar(string icon, string label, double percentage, string color)
    {
        return $@"
            <div style=""margin-bottom: 12px;"">
                <div style=""display: flex; justify-content: space-between; font-size: 12px; margin-bottom: 4px;"">
                    <span>{icon} {label}</span>
                    <span>{percentage:F0}%</span>
                </div>
                <div style=""width: 100%; background: var(--neutral-layer-3); border-radius: 4px; height: 8px;"">
                    <div style=""width: {percentage}%; background: {color}; border-radius: 4px; height: 100%; transition: width 0.3s;""></div>
                </div>
            </div>";
    }

    private static void ShowNotification(LayoutAreaHost host, string message, string backgroundColor, int dismissAfterMs = 3000)
    {
        var notification = Controls.Html($@"
            <div style=""position: fixed; top: 20px; right: 20px; padding: 12px 20px;
                        background: {backgroundColor}; color: white; border-radius: 6px;
                        box-shadow: 0 4px 12px rgba(0,0,0,0.3); z-index: 10000;
                        animation: slideIn 0.3s ease-out;"">
                <style>
                    @keyframes slideIn {{
                        from {{ transform: translateX(100%); opacity: 0; }}
                        to {{ transform: translateX(0); opacity: 1; }}
                    }}
                </style>
                {message}
            </div>");
        host.UpdateArea("$Notification", notification);

        _ = System.Threading.Tasks.Task.Delay(dismissAfterMs).ContinueWith(_ =>
            host.UpdateArea("$Notification", null!));
    }

    #endregion

    #region Action Handlers

    private static async System.Threading.Tasks.Task RestoreTodo(LayoutAreaHost host, string todoPath)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        var persistence = host.Hub.ServiceProvider.GetService<MeshWeaver.Mesh.Services.IPersistenceService>();
        if (meshCatalog == null || persistence == null) return;

        var existingNode = await meshCatalog.GetNodeAsync(new Address(todoPath));
        if (existingNode == null) return;

        var restoredNode = existingNode with { State = MeshNodeState.Active };
        await persistence.SaveNodeAsync(restoredNode);
    }

    private static async System.Threading.Tasks.Task HardDeleteTodo(LayoutAreaHost host, string todoPath)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        if (meshCatalog == null) return;
        await meshCatalog.DeleteNodeAsync(todoPath);
    }

    private static void ShowPermanentDeleteConfirmationDialog(LayoutAreaHost host, TodoItem todo, string todoPath)
    {
        var content = Controls.Stack
            .WithView(Controls.Html($@"
                <div style=""text-align: center; padding: 16px;"">
                    <div style=""font-size: 48px; margin-bottom: 16px;"">⚠️</div>
                    <p>Permanently delete <strong>{System.Web.HttpUtility.HtmlEncode(todo.Title)}</strong>?</p>
                    <p style=""color: #dc3545; font-size: 14px; font-weight: 600;"">
                        This action cannot be undone!
                    </p>
                </div>"))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle(s => s.WithJustifyContent("center").WithGap("12px"))
                .WithView(Controls.Button("Cancel").WithAppearance(Appearance.Neutral)
                    .WithClickAction(_ => { host.UpdateArea(DialogControl.DialogArea, null!); return System.Threading.Tasks.Task.CompletedTask; }))
                .WithView(Controls.Button("Delete Permanently").WithAppearance(Appearance.Accent)
                    .WithStyle(s => s.WithBackgroundColor("#dc3545"))
                    .WithClickAction(_ => {
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return HardDeleteTodo(host, todoPath);
                    })));

        host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(content, "Permanent Delete").WithSize("S").WithClosable(false));
    }

    #endregion
}
