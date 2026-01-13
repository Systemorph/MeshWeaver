// <meshweaver>
// Id: ProjectViews
// DisplayName: Project Views
// </meshweaver>

/// <summary>
/// Task completion status.
/// </summary>
public enum TodoStatus
{
    Pending,
    InProgress,
    InReview,
    Completed,
    Blocked
}

/// <summary>
/// Task priority level.
/// </summary>
public enum TaskPriority
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Todo record for deserialization.
/// </summary>
public record Todo : IContentInitializable
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Category { get; init; } = "General";
    public TaskPriority Priority { get; init; } = TaskPriority.Medium;
    public string? Assignee { get; init; }
    public DateTimeOffset? DueDate { get; init; }
    public int? DueDateOffsetDays { get; init; }
    public TodoStatus Status { get; init; } = TodoStatus.Pending;
    public string Icon { get; init; } = "TaskListSquare";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }

    public object Initialize()
    {
        if (DueDateOffsetDays.HasValue)
        {
            return this with { DueDate = DateTimeOffset.UtcNow.Date.AddDays(DueDateOffsetDays.Value) };
        }
        return this;
    }
}

/// <summary>
/// Custom views for Project nodes showing aggregated Todo item statistics and management.
/// </summary>
public static class ProjectViews
{
    /// <summary>
    /// Summary view showing aggregated statistics for all child Todo items.
    /// </summary>
    [Display(GroupName = "Overview", Order = 1)]
    public static IObservable<UiControl?> Summary(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return Observable.FromAsync(async () =>
        {
            var todos = await LoadChildTodos(hubPath, host);
            return BuildSummaryView(todos, hubPath);
        });
    }

    private static UiControl BuildSummaryView(List<Todo> todos, string hubPath)
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
        var completedCount = todos.Count(t => t.Status == TodoStatus.Completed);
        var completionRate = totalCount > 0 ? (completedCount * 100.0 / totalCount) : 0;

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"**Total Tasks:** {totalCount} | **Completed:** {completedCount} ({completionRate:F0}%)"),
                skin => skin.WithXs(12));

        // Status breakdown
        mainGrid = mainGrid
            .WithView(Controls.H5("\ud83d\udcc8 Status Overview")
                .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("8px")),
                skin => skin.WithXs(12));

        var statusGroups = todos.GroupBy(t => t.Status).OrderBy(g => (int)g.Key);
        foreach (var group in statusGroups)
        {
            var icon = GetStatusIcon(group.Key);
            var percentage = (group.Count() * 100.0 / totalCount).ToString("F1");
            mainGrid = mainGrid
                .WithView(Controls.Markdown($"{icon} **{group.Key}**: {group.Count()} ({percentage}%)")
                    .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("4px")),
                    skin => skin.WithXs(12));
        }

        // Due date analysis
        var now = DateTimeOffset.Now.Date;
        var overdue = todos.Count(t => t.DueDate?.Date < now && t.Status != TodoStatus.Completed);
        var dueToday = todos.Count(t => t.DueDate?.Date == now && t.Status != TodoStatus.Completed);
        var dueSoon = todos.Count(t => t.DueDate?.Date > now && t.DueDate?.Date <= now.AddDays(7) && t.Status != TodoStatus.Completed);

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
        var criticalCount = todos.Count(t => t.Priority == TaskPriority.Critical && t.Status != TodoStatus.Completed);
        var highCount = todos.Count(t => t.Priority == TaskPriority.High && t.Status != TodoStatus.Completed);

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
        var assignees = todos.Where(t => !string.IsNullOrEmpty(t.Assignee) && t.Status != TodoStatus.Completed)
            .GroupBy(t => t.Assignee!)
            .OrderByDescending(g => g.Count())
            .Take(5);

        var unassigned = todos.Count(t => string.IsNullOrEmpty(t.Assignee) && t.Status != TodoStatus.Completed);

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
    /// AllItems view listing all child Todo items with status grouping and actions.
    /// </summary>
    [Display(GroupName = "Overview", Order = 2)]
    public static IObservable<UiControl?> AllItems(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.FromAsync(async () =>
        {
            var todos = await LoadChildTodos(hubPath, host);
            return BuildAllItemsView(todos, host, hubPath);
        });
    }

    private static UiControl BuildAllItemsView(List<Todo> todos, LayoutAreaHost host, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83d\udcdd All Tasks")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));

        if (!todos.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("*No tasks found.*"),
                skin => skin.WithXs(12));
        }

        // Group by status
        var statusOrder = new[] { TodoStatus.InProgress, TodoStatus.Pending, TodoStatus.InReview, TodoStatus.Blocked, TodoStatus.Completed };
        foreach (var status in statusOrder)
        {
            var statusTodos = todos.Where(t => t.Status == status)
                .OrderBy(t => t.DueDate ?? DateTimeOffset.MaxValue)
                .ThenBy(t => t.CreatedAt)
                .ToList();

            if (!statusTodos.Any()) continue;

            var icon = GetStatusIcon(status);
            var isExpanded = status != TodoStatus.Completed;

            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"{icon} {status} ({statusTodos.Count})", statusTodos, hubPath, isExpanded),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// TodosByCategory view grouping items by category.
    /// </summary>
    [Display(GroupName = "Overview", Order = 3)]
    public static IObservable<UiControl?> TodosByCategory(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.FromAsync(async () =>
        {
            var todos = await LoadChildTodos(hubPath, host);
            return BuildTodosByCategoryView(todos, hubPath);
        });
    }

    private static UiControl BuildTodosByCategoryView(List<Todo> todos, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83d\udcc2 Tasks by Category")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));

        if (!todos.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("*No tasks found.*"),
                skin => skin.WithXs(12));
        }

        var categoryGroups = todos
            .GroupBy(t => string.IsNullOrEmpty(t.Category) ? "Uncategorized" : t.Category)
            .OrderByDescending(g => g.Count());

        foreach (var group in categoryGroups)
        {
            var completedCount = group.Count(t => t.Status == TodoStatus.Completed);
            var inProgressCount = group.Count(t => t.Status == TodoStatus.InProgress);
            var pendingCount = group.Count(t => t.Status == TodoStatus.Pending);

            var statusIndicator = $"\u2705{completedCount} \ud83d\udd04{inProgressCount} \u23f3{pendingCount}";
            var categoryTodos = group
                .OrderBy(t => (int)t.Status)
                .ThenBy(t => t.DueDate ?? DateTimeOffset.MaxValue)
                .ToList();

            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\ud83d\udcc1 {group.Key} ({group.Count()}) - {statusIndicator}", categoryTodos, hubPath, true),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// Planning view for workload management and task assignment.
    /// </summary>
    [Display(GroupName = "Planning")]
    public static IObservable<UiControl?> Planning(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.FromAsync(async () =>
        {
            var todos = await LoadChildTodos(hubPath, host);
            return BuildPlanningView(todos, host, hubPath);
        });
    }

    private static UiControl BuildPlanningView(List<Todo> todos, LayoutAreaHost host, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83c\udfaf Planning & Assignment")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));

        if (!todos.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("*No tasks found. Create some tasks to start planning!*"),
                skin => skin.WithXs(12));
        }

        var activeTodos = todos.Where(t => t.Status != TodoStatus.Completed).ToList();

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
            var overdueCount = assignee.Count(t => t.DueDate?.Date < DateTimeOffset.Now.Date);
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
            .OrderBy(t => t.DueDate ?? DateTimeOffset.MaxValue)
            .ToList();

        if (unassignedTasks.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.H5($"\ud83d\udccb Unassigned Tasks ({unassignedTasks.Count})")
                    .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("12px").WithColor("#ffc107")),
                    skin => skin.WithXs(12));

            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection("View unassigned tasks", unassignedTasks, hubPath, true),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// MyTasks view showing current user's active tasks.
    /// </summary>
    [Display(GroupName = "My Work")]
    public static IObservable<UiControl?> MyTasks(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.FromAsync(async () =>
        {
            var todos = await LoadChildTodos(hubPath, host);
            return BuildMyTasksView(todos, hubPath, host);
        });
    }

    private static UiControl BuildMyTasksView(List<Todo> todos, string hubPath, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83d\udfe2 My Tasks")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));

        // Get current user from AccessService
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? accessService?.Context?.ObjectId ?? "Guest";
        var myTasks = todos
            .Where(t => t.Assignee == currentUser && t.Status != TodoStatus.Completed)
            .ToList();

        if (!myTasks.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown($"*No active tasks assigned to {currentUser}. Great job!*"),
                skin => skin.WithXs(12));
        }

        var now = DateTimeOffset.Now.Date;

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
            .OrderBy(t => t.DueDate ?? DateTimeOffset.MaxValue)
            .ToList();

        // Summary
        var inProgressCount = myTasks.Count(t => t.Status == TodoStatus.InProgress);
        var overdueCount = urgentTasks.Count(t => t.DueDate?.Date < now);

        mainGrid = mainGrid.WithView(
            Controls.Markdown($"**Active**: {myTasks.Count} | **In Progress**: {inProgressCount} | **Overdue**: {overdueCount}")
                .WithStyle(style => style.WithMarginBottom("16px")),
            skin => skin.WithXs(12));

        if (urgentTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\ud83d\udea8 Urgent ({urgentTasks.Count})", urgentTasks, hubPath, true),
                skin => skin.WithXs(12));
        }

        if (tomorrowTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\ud83d\udcc5 Tomorrow ({tomorrowTasks.Count})", tomorrowTasks, hubPath, true),
                skin => skin.WithXs(12));
        }

        if (upcomingTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\ud83d\uddd3\ufe0f Upcoming ({upcomingTasks.Count})", upcomingTasks, hubPath, true),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// Backlog view showing unassigned tasks.
    /// </summary>
    [Display(GroupName = "Planning")]
    public static IObservable<UiControl?> Backlog(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.FromAsync(async () =>
        {
            var todos = await LoadChildTodos(hubPath, host);
            return BuildBacklogView(todos, host, hubPath);
        });
    }

    private static UiControl BuildBacklogView(List<Todo> todos, LayoutAreaHost host, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83d\udccb Backlog - Unassigned Tasks")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));

        var backlogTasks = todos
            .Where(t => string.IsNullOrEmpty(t.Assignee) && t.Status != TodoStatus.Completed)
            .OrderBy(t => t.DueDate ?? DateTimeOffset.MaxValue)
            .ThenBy(t => (int)t.Priority)
            .ToList();

        if (!backlogTasks.Any())
        {
            return mainGrid.WithView(
                Controls.Markdown("*No unassigned tasks. All tasks have been assigned!*"),
                skin => skin.WithXs(12));
        }

        mainGrid = mainGrid.WithView(
            Controls.Markdown($"**{backlogTasks.Count} tasks** waiting to be assigned")
                .WithStyle(style => style.WithMarginBottom("16px")),
            skin => skin.WithXs(12));

        // Group by priority
        var criticalTasks = backlogTasks.Where(t => t.Priority == TaskPriority.Critical).ToList();
        var highTasks = backlogTasks.Where(t => t.Priority == TaskPriority.High).ToList();
        var normalTasks = backlogTasks.Where(t => t.Priority == TaskPriority.Medium || t.Priority == TaskPriority.Low).ToList();

        if (criticalTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\ud83d\udea8 Critical Priority ({criticalTasks.Count})", criticalTasks, hubPath, true),
                skin => skin.WithXs(12));
        }

        if (highTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\ud83d\udd25 High Priority ({highTasks.Count})", highTasks, hubPath, true),
                skin => skin.WithXs(12));
        }

        if (normalTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\u2796 Normal Priority ({normalTasks.Count})", normalTasks, hubPath, false),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    /// <summary>
    /// TodaysFocus view showing urgent items (overdue + due today + in-progress).
    /// </summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> TodaysFocus(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.FromAsync(async () =>
        {
            var todos = await LoadChildTodos(hubPath, host);
            return BuildTodaysFocusView(todos, hubPath);
        });
    }

    private static UiControl BuildTodaysFocusView(List<Todo> todos, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83c\udfaf Today's Focus")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(12));

        var now = DateTimeOffset.Now.Date;
        var activeTodos = todos.Where(t => t.Status != TodoStatus.Completed).ToList();

        // Overdue items
        var overdue = activeTodos
            .Where(t => t.DueDate?.Date < now)
            .OrderBy(t => t.DueDate)
            .ToList();

        // Due today
        var dueToday = activeTodos
            .Where(t => t.DueDate?.Date == now)
            .OrderBy(t => (int)t.Priority)
            .ToList();

        // In progress (regardless of due date)
        var inProgress = activeTodos
            .Where(t => t.Status == TodoStatus.InProgress && t.DueDate?.Date != now && t.DueDate?.Date >= now)
            .OrderBy(t => t.DueDate ?? DateTimeOffset.MaxValue)
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
                BuildCollapsibleSection($"\ud83d\udea8 Overdue ({overdue.Count})", overdue, hubPath, true),
                skin => skin.WithXs(12));
        }

        if (dueToday.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\u23f0 Due Today ({dueToday.Count})", dueToday, hubPath, true),
                skin => skin.WithXs(12));
        }

        if (inProgress.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSection($"\ud83d\udd04 In Progress ({inProgress.Count})", inProgress, hubPath, true),
                skin => skin.WithXs(12));
        }

        return mainGrid;
    }

    // Helper methods

    private static async System.Threading.Tasks.Task<List<Todo>> LoadChildTodos(string hubPath, LayoutAreaHost host)
    {
        var todos = new List<Todo>();

        // Use IMeshQuery directly to query for child Todo items
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery == null)
            return todos;

        var query = $"path:{hubPath}/Todo nodeType:ACME/Project/Todo scope:subtree";
        var request = MeshQueryRequest.FromQuery(query);

        await foreach (var result in meshQuery.QueryAsync<MeshNode>(request))
        {
            var todo = result.GetContent<Todo>();
            if (todo != null)
            {
                // Initialize to compute DueDate from DueDateOffsetDays if set
                todo = (Todo)todo.Initialize();
                todos.Add(todo);
            }
        }

        return todos;
    }

    private static UiControl BuildCollapsibleSection(string title, List<Todo> todos, string hubPath, bool defaultOpen)
    {
        var itemsHtml = new System.Text.StringBuilder();
        foreach (var todo in todos)
        {
            var statusIcon = GetStatusIcon(todo.Status);
            var priorityBadge = GetPriorityBadge(todo.Priority);
            var dueInfo = todo.DueDate.HasValue ? $"Due: {todo.DueDate.Value:MMM dd}" : "";
            var assignee = todo.Assignee ?? "Unassigned";
            var todoPath = $"{hubPath}/Todo/{todo.Id}";

            itemsHtml.AppendLine($@"
                <a href=""/{todoPath}"" style=""text-decoration: none; color: inherit; display: block;"">
                    <div style=""padding: 8px 12px; border-bottom: 1px solid var(--neutral-stroke-rest); display: flex; align-items: center; gap: 8px;"">
                        <span>{statusIcon}</span>
                        <span style=""flex: 1;"">{System.Web.HttpUtility.HtmlEncode(todo.Title)}</span>
                        {priorityBadge}
                        <span style=""font-size: 12px; color: var(--neutral-foreground-hint);"">{assignee}</span>
                        <span style=""font-size: 11px; color: var(--neutral-foreground-hint);"">{dueInfo}</span>
                    </div>
                </a>");
        }

        var openAttr = defaultOpen ? " open" : "";
        return Controls.Html($@"
            <details{openAttr} style=""margin-bottom: 16px;"">
                <summary style=""cursor: pointer; padding: 8px 12px; background: var(--neutral-layer-2); border-radius: 6px; font-weight: 600;"">
                    {title}
                </summary>
                <div style=""border: 1px solid var(--neutral-stroke-rest); border-radius: 0 0 6px 6px; margin-top: -1px;"">
                    {itemsHtml}
                </div>
            </details>");
    }

    private static string GetStatusIcon(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "\u23f3",
        TodoStatus.InProgress => "\ud83d\udd04",
        TodoStatus.InReview => "\ud83d\udc41\ufe0f",
        TodoStatus.Completed => "\u2705",
        TodoStatus.Blocked => "\ud83d\udeab",
        _ => "\u2753"
    };

    private static string GetPriorityBadge(TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => "<span style=\"background: #dc3545; color: white; padding: 1px 4px; border-radius: 3px; font-size: 10px;\">CRITICAL</span>",
        TaskPriority.High => "<span style=\"background: #fd7e14; color: white; padding: 1px 4px; border-radius: 3px; font-size: 10px;\">HIGH</span>",
        _ => ""
    };
}
