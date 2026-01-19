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
    // Shared refresh trigger for real-time updates across views
    private static readonly System.Reactive.Subjects.Subject<long> _refreshTrigger = new();

    /// <summary>
    /// Triggers a refresh of all views that subscribe to the refresh mechanism.
    /// </summary>
    public static void TriggerRefresh() => _refreshTrigger.OnNext(DateTimeOffset.UtcNow.Ticks);

    /// <summary>
    /// Summary view showing aggregated statistics for all child Todo items.
    /// </summary>
    [Display(GroupName = "Overview", Order = 1)]
    public static IObservable<UiControl?> Summary(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return _refreshTrigger
            .StartWith(0)
            .SelectMany(_ => Observable.FromAsync(async () =>
            {
                var todos = await LoadChildTodos(hubPath, host);
                return BuildSummaryView(todos, hubPath);
            }));
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

        // Visual progress bars
        var inProgressCount = todos.Count(t => t.Status == TodoStatus.InProgress);
        var blockedCount = todos.Count(t => t.Status == TodoStatus.Blocked);
        var inProgressRate = totalCount > 0 ? (inProgressCount * 100.0 / totalCount) : 0;
        var blockedRate = totalCount > 0 ? (blockedCount * 100.0 / totalCount) : 0;

        mainGrid = mainGrid
            .WithView(Controls.Html($@"
                <div style=""margin: 16px 0; padding: 12px; background: var(--neutral-layer-2); border-radius: 8px;"">
                    <div style=""margin-bottom: 12px;"">
                        <div style=""display: flex; justify-content: space-between; font-size: 12px; margin-bottom: 4px;"">
                            <span>✅ Completion</span>
                            <span>{completionRate:F0}%</span>
                        </div>
                        <div style=""width: 100%; background: var(--neutral-layer-3); border-radius: 4px; height: 8px;"">
                            <div style=""width: {completionRate}%; background: #28a745; border-radius: 4px; height: 100%; transition: width 0.3s;""></div>
                        </div>
                    </div>
                    <div style=""margin-bottom: 12px;"">
                        <div style=""display: flex; justify-content: space-between; font-size: 12px; margin-bottom: 4px;"">
                            <span>🔄 In Progress</span>
                            <span>{inProgressRate:F0}%</span>
                        </div>
                        <div style=""width: 100%; background: var(--neutral-layer-3); border-radius: 4px; height: 8px;"">
                            <div style=""width: {inProgressRate}%; background: #007bff; border-radius: 4px; height: 100%; transition: width 0.3s;""></div>
                        </div>
                    </div>
                    <div>
                        <div style=""display: flex; justify-content: space-between; font-size: 12px; margin-bottom: 4px;"">
                            <span>🚫 Blocked</span>
                            <span>{blockedRate:F0}%</span>
                        </div>
                        <div style=""width: 100%; background: var(--neutral-layer-3); border-radius: 4px; height: 8px;"">
                            <div style=""width: {blockedRate}%; background: #dc3545; border-radius: 4px; height: 100%; transition: width 0.3s;""></div>
                        </div>
                    </div>
                </div>"),
                skin => skin.WithXs(12));

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
        return _refreshTrigger
            .StartWith(0)
            .SelectMany(_ => Observable.FromAsync(async () =>
            {
                var todos = await LoadChildTodos(hubPath, host);
                return BuildAllItemsView(todos, host, hubPath);
            }));
    }

    private static UiControl BuildAllItemsView(List<Todo> todos, LayoutAreaHost host, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header row with title and New Task button
        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83d\udcdd All Tasks")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(8).WithMd(10))
            .WithView(BuildNewTaskButton(host, hubPath),
                skin => skin.WithXs(4).WithMd(2));

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
                BuildCollapsibleSectionWithActions($"{icon} {status} ({statusTodos.Count})", statusTodos, hubPath, isExpanded, host),
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
        return _refreshTrigger
            .StartWith(0)
            .SelectMany(_ => Observable.FromAsync(async () =>
            {
                var todos = await LoadChildTodos(hubPath, host);
                return BuildTodosByCategoryView(todos, hubPath, host);
            }));
    }

    private static UiControl BuildTodosByCategoryView(List<Todo> todos, string hubPath, LayoutAreaHost host)
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
                BuildCollapsibleSectionWithActions($"\ud83d\udcc1 {group.Key} ({group.Count()}) - {statusIndicator}", categoryTodos, hubPath, true, host),
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
        return _refreshTrigger
            .StartWith(0)
            .SelectMany(_ => Observable.FromAsync(async () =>
            {
                var todos = await LoadChildTodos(hubPath, host);
                return BuildPlanningView(todos, host, hubPath);
            }));
    }

    private static UiControl BuildPlanningView(List<Todo> todos, LayoutAreaHost host, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header row with title and New Task button
        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83c\udfaf Planning & Assignment")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(8).WithMd(10))
            .WithView(BuildNewTaskButton(host, hubPath),
                skin => skin.WithXs(4).WithMd(2));

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
                BuildCollapsibleSectionWithActions("View unassigned tasks", unassignedTasks, hubPath, true, host),
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
        return _refreshTrigger
            .StartWith(0)
            .SelectMany(_ => Observable.FromAsync(async () =>
            {
                var todos = await LoadChildTodos(hubPath, host);
                return BuildMyTasksView(todos, hubPath, host);
            }));
    }

    private static UiControl BuildMyTasksView(List<Todo> todos, string hubPath, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header row with title and New Task button
        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83d\udfe2 My Tasks")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(8).WithMd(10))
            .WithView(BuildNewTaskButton(host, hubPath),
                skin => skin.WithXs(4).WithMd(2));

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
                BuildCollapsibleSectionWithActions($"\ud83d\udea8 Urgent ({urgentTasks.Count})", urgentTasks, hubPath, true, host),
                skin => skin.WithXs(12));
        }

        if (tomorrowTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionWithActions($"\ud83d\udcc5 Tomorrow ({tomorrowTasks.Count})", tomorrowTasks, hubPath, true, host),
                skin => skin.WithXs(12));
        }

        if (upcomingTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionWithActions($"\ud83d\uddd3\ufe0f Upcoming ({upcomingTasks.Count})", upcomingTasks, hubPath, true, host),
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
        return _refreshTrigger
            .StartWith(0)
            .SelectMany(_ => Observable.FromAsync(async () =>
            {
                var todos = await LoadChildTodos(hubPath, host);
                return BuildBacklogView(todos, host, hubPath);
            }));
    }

    private static UiControl BuildBacklogView(List<Todo> todos, LayoutAreaHost host, string hubPath)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header row with title and New Task button
        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83d\udccb Backlog - Unassigned Tasks")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(8).WithMd(10))
            .WithView(BuildNewTaskButton(host, hubPath),
                skin => skin.WithXs(4).WithMd(2));

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
                BuildCollapsibleSectionWithActions($"\ud83d\udea8 Critical Priority ({criticalTasks.Count})", criticalTasks, hubPath, true, host),
                skin => skin.WithXs(12));
        }

        if (highTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionWithActions($"\ud83d\udd25 High Priority ({highTasks.Count})", highTasks, hubPath, true, host),
                skin => skin.WithXs(12));
        }

        if (normalTasks.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionWithActions($"\u2796 Normal Priority ({normalTasks.Count})", normalTasks, hubPath, false, host),
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
        return _refreshTrigger
            .StartWith(0)
            .SelectMany(_ => Observable.FromAsync(async () =>
            {
                var todos = await LoadChildTodos(hubPath, host);
                return BuildTodaysFocusView(todos, hubPath, host);
            }));
    }

    private static UiControl BuildTodaysFocusView(List<Todo> todos, string hubPath, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header row with title and New Task button
        mainGrid = mainGrid
            .WithView(Controls.H4("\ud83c\udfaf Today's Focus")
                .WithStyle(style => style.WithMarginBottom("16px")),
                skin => skin.WithXs(8).WithMd(10))
            .WithView(BuildNewTaskButton(host, hubPath),
                skin => skin.WithXs(4).WithMd(2));

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
                BuildCollapsibleSectionWithActions($"\ud83d\udea8 Overdue ({overdue.Count})", overdue, hubPath, true, host),
                skin => skin.WithXs(12));
        }

        if (dueToday.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionWithActions($"\u23f0 Due Today ({dueToday.Count})", dueToday, hubPath, true, host),
                skin => skin.WithXs(12));
        }

        if (inProgress.Any())
        {
            mainGrid = mainGrid.WithView(
                BuildCollapsibleSectionWithActions($"\ud83d\udd04 In Progress ({inProgress.Count})", inProgress, hubPath, true, host),
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
        return BuildCollapsibleSectionWithActions(title, todos, hubPath, defaultOpen, null);
    }

    private static UiControl BuildCollapsibleSectionWithActions(string title, List<Todo> todos, string hubPath, bool defaultOpen, LayoutAreaHost? host)
    {
        var openAttr = defaultOpen ? " open" : "";

        // If no host provided, fall back to simple HTML links (no action buttons)
        if (host == null)
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

        // With host, create a Stack with section header and interactive items
        var sectionStack = Controls.Stack
            .WithStyle(style => style.WithMarginBottom("16px"));

        // Section header
        sectionStack = sectionStack.WithView(Controls.Html($@"
            <div style=""padding: 8px 12px; background: var(--neutral-layer-2); border-radius: 6px; font-weight: 600; margin-bottom: 4px;"">
                {title}
            </div>"));

        // Items container
        var itemsStack = Controls.Stack
            .WithStyle(style => style.WithBorder("1px solid var(--neutral-stroke-rest)").WithBorderRadius("6px"));

        foreach (var todo in todos)
        {
            itemsStack = itemsStack.WithView(BuildTodoListItem(todo, hubPath, host));
        }

        sectionStack = sectionStack.WithView(itemsStack);

        return sectionStack;
    }

    private static UiControl BuildTodoListItem(Todo todo, string hubPath, LayoutAreaHost host)
    {
        var statusIcon = GetStatusIcon(todo.Status);
        var priorityBadge = GetPriorityBadge(todo.Priority);
        var dueInfo = todo.DueDate.HasValue ? $"Due: {todo.DueDate.Value:MMM dd}" : "";
        var assignee = todo.Assignee ?? "Unassigned";
        var todoPath = $"{hubPath}/Todo/{todo.Id}";
        var todoAddress = new Address(todoPath);

        // Main row with info and actions
        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(style => style
                .WithPadding("8px 12px")
                .WithBorderBottom("1px solid var(--neutral-stroke-rest)")
                .WithAlignItems("center")
                .WithGap("8px"));

        // Status icon
        row = row.WithView(Controls.Html($"<span style=\"font-size: 16px;\">{statusIcon}</span>"));

        // Title as link (takes most space)
        row = row.WithView(Controls.Html($@"
            <a href=""/{todoPath}"" style=""text-decoration: none; color: inherit; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"">
                {System.Web.HttpUtility.HtmlEncode(todo.Title)}
            </a>"));

        // Priority badge
        if (!string.IsNullOrEmpty(priorityBadge))
        {
            row = row.WithView(Controls.Html(priorityBadge));
        }

        // Assignee
        row = row.WithView(Controls.Html($"<span style=\"font-size: 12px; color: var(--neutral-foreground-hint); min-width: 70px;\">{assignee}</span>"));

        // Due date
        if (!string.IsNullOrEmpty(dueInfo))
        {
            row = row.WithView(Controls.Html($"<span style=\"font-size: 11px; color: var(--neutral-foreground-hint);\">{dueInfo}</span>"));
        }

        // Quick action: Status transition button
        var nextStatus = GetNextStatus(todo.Status);
        var statusButtonIcon = todo.Status == TodoStatus.Completed ? "↩️" : "✅";
        var statusButtonTitle = todo.Status == TodoStatus.Completed ? "Reopen" : $"Mark {nextStatus}";
        row = row.WithView(
            Controls.Button(statusButtonIcon)
                .WithLabel(statusButtonTitle)
                .WithAppearance(Appearance.Stealth)
                .WithStyle(style => style.WithMinWidth("28px").WithPadding("2px 6px"))
                .WithClickAction(actx =>
                {
                    var updatedTodo = todo with
                    {
                        Status = nextStatus,
                        CompletedAt = nextStatus == TodoStatus.Completed ? DateTimeOffset.UtcNow : null
                    };
                    return UpdateTodoViaNode(actx.Host, updatedTodo, todoPath).ContinueWith(_ => TriggerRefresh());
                }));

        // Edit button
        row = row.WithView(
            Controls.Button("✏️")
                .WithLabel("Edit")
                .WithAppearance(Appearance.Stealth)
                .WithStyle(style => style.WithMinWidth("28px").WithPadding("2px 6px"))
                .WithClickAction(actx =>
                {
                    OpenEditTodoDialog(actx.Host, todo, todoAddress);
                    return System.Threading.Tasks.Task.CompletedTask;
                }));

        // Quick action: Delete button
        row = row.WithView(
            Controls.Button("🗑️")
                .WithLabel("Delete")
                .WithAppearance(Appearance.Stealth)
                .WithStyle(style => style.WithMinWidth("28px").WithPadding("2px 6px").WithColor("#dc3545"))
                .WithClickAction(actx =>
                {
                    return DeleteTodoViaNode(actx.Host, todoPath).ContinueWith(_ => TriggerRefresh());
                }));

        return row;
    }

    private static TodoStatus GetNextStatus(TodoStatus current) => current switch
    {
        TodoStatus.Pending => TodoStatus.InProgress,
        TodoStatus.InProgress => TodoStatus.InReview,
        TodoStatus.InReview => TodoStatus.Completed,
        TodoStatus.Completed => TodoStatus.Pending,
        TodoStatus.Blocked => TodoStatus.InProgress,
        _ => TodoStatus.Pending
    };

    private static async System.Threading.Tasks.Task UpdateTodoViaNode(LayoutAreaHost host, Todo todo, string todoPath)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        if (meshCatalog == null) return;

        // Get existing node
        var existingNode = await meshCatalog.GetNodeAsync(new Address(todoPath));
        if (existingNode == null) return;

        // Update content and save via persistence
        var updatedNode = existingNode with { Content = todo };
        await meshCatalog.Persistence.SaveNodeAsync(updatedNode);
    }

    private static async System.Threading.Tasks.Task DeleteTodoViaNode(LayoutAreaHost host, string todoPath)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        if (meshCatalog == null) return;

        await meshCatalog.DeleteNodeAsync(todoPath);
    }

    private static async System.Threading.Tasks.Task HandleSaveEditedTodo(LayoutAreaHost host, Todo originalTodo, string todoPath, string editDataId)
    {
        // Retrieve edited data from the store
        var store = host.Stream.Current?.Value;
        var dataCollection = store?.GetCollection(LayoutAreaReference.Data);
        var rawData = dataCollection?.Instances.GetValueOrDefault(editDataId);

        Todo? editedTodo = null;
        if (rawData is Todo t)
            editedTodo = t;
        else if (rawData is System.Text.Json.JsonElement jsonElement)
            editedTodo = System.Text.Json.JsonSerializer.Deserialize<Todo>(jsonElement.GetRawText());

        if (editedTodo != null)
        {
            // Ensure the ID is preserved
            editedTodo = editedTodo with { Id = originalTodo.Id };

            // Save via persistence
            await UpdateTodoViaNode(host, editedTodo, todoPath);

            // Trigger refresh of all views after edit
            TriggerRefresh();
        }

        host.UpdateArea(DialogControl.DialogArea, null!);
    }

    private static async System.Threading.Tasks.Task HandleCreateTodo(LayoutAreaHost host, string hubPath, string createDataId)
    {
        System.Console.WriteLine($"Create button clicked, createDataId: {createDataId}");

        // Get the edited todo from the stream's current EntityStore
        var store = host.Stream.Current?.Value;
        System.Console.WriteLine($"Store is null: {store == null}");

        // Get data from the "data" collection by id
        var dataCollection = store?.GetCollection(LayoutAreaReference.Data);
        System.Console.WriteLine($"DataCollection is null: {dataCollection == null}");

        var rawData = dataCollection?.Instances.GetValueOrDefault(createDataId);
        System.Console.WriteLine($"RawData is null: {rawData == null}, type: {rawData?.GetType().Name}");

        // Convert the raw data to Todo
        Todo? editedTodo = null;
        if (rawData is Todo todo)
        {
            editedTodo = todo;
        }
        else if (rawData is System.Text.Json.JsonElement jsonElement)
        {
            editedTodo = System.Text.Json.JsonSerializer.Deserialize<Todo>(jsonElement.GetRawText());
        }

        System.Console.WriteLine($"EditedTodo is null: {editedTodo == null}");

        if (editedTodo != null)
        {
            System.Console.WriteLine($"Creating todo: {editedTodo.Id} - {editedTodo.Title}");

            // Create the MeshNode at the correct path: {hubPath}/Todo/{id}
            var todoPath = $"{hubPath}/Todo";
            var meshNode = new MeshNode(editedTodo.Id, todoPath)
            {
                Name = editedTodo.Title,
                NodeType = $"{hubPath.Split('/')[0]}/Project/Todo",
                Content = editedTodo,
                IsPersistent = true,
                Category = "Tasks"
            };

            System.Console.WriteLine($"MeshNode path: {meshNode.Path}, NodeType: {meshNode.NodeType}");

            // Use IMeshCatalog to create the node
            var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
            System.Console.WriteLine($"MeshCatalog is null: {meshCatalog == null}");

            if (meshCatalog != null)
            {
                try
                {
                    System.Console.WriteLine("Calling CreateNodeAsync...");
                    var result = await meshCatalog.CreateNodeAsync(meshNode);
                    System.Console.WriteLine($"CreateNodeAsync completed, result path: {result?.Path}");

                    // Show success notification with updated count
                    var updatedTodos = await LoadChildTodos(hubPath, host);
                    var notification = Controls.Html($@"
                        <div style=""position: fixed; top: 20px; right: 20px; padding: 12px 20px;
                                    background: #28a745; color: white; border-radius: 6px;
                                    box-shadow: 0 4px 12px rgba(0,0,0,0.3); z-index: 10000;
                                    animation: slideIn 0.3s ease-out;"">
                            <style>
                                @keyframes slideIn {{
                                    from {{ transform: translateX(100%); opacity: 0; }}
                                    to {{ transform: translateX(0); opacity: 1; }}
                                }}
                            </style>
                            ✅ Task created! Total tasks: {updatedTodos.Count}
                        </div>");
                    host.UpdateArea("$Notification", notification);

                    // Trigger refresh of all views
                    TriggerRefresh();

                    // Auto-dismiss notification after 3 seconds
                    _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                        host.UpdateArea("$Notification", null!));
                }
                catch (System.Exception ex)
                {
                    System.Console.WriteLine($"Error creating node: {ex.Message}");
                    System.Console.WriteLine($"Stack trace: {ex.StackTrace}");

                    // Show error notification
                    var errorNotification = Controls.Html($@"
                        <div style=""position: fixed; top: 20px; right: 20px; padding: 12px 20px;
                                    background: #dc3545; color: white; border-radius: 6px;
                                    box-shadow: 0 4px 12px rgba(0,0,0,0.3); z-index: 10000;"">
                            ❌ Failed to create task: {System.Web.HttpUtility.HtmlEncode(ex.Message)}
                        </div>");
                    host.UpdateArea("$Notification", errorNotification);

                    _ = System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                        host.UpdateArea("$Notification", null!));
                }
            }
        }
        else
        {
            System.Console.WriteLine("EditedTodo is null, cannot create node");
        }
        host.UpdateArea(DialogControl.DialogArea, null!);
    }

    private static void OpenEditTodoDialog(LayoutAreaHost host, Todo todo, Address todoAddress)
    {
        var editDataId = $"EditTodo_{todo.Id}";
        var todoPath = todoAddress.ToString();

        var editForm = Controls.Stack
            .WithView(Controls.H5("Edit Task")
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(host.Edit(todo, editDataId)?
                .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), editDataId)
            .WithView(Controls.Stack
                .WithView(Controls.Button("Save")
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(_ => HandleSaveEditedTodo(host, todo, todoPath, editDataId)))
                .WithView(Controls.Button("Cancel")
                    .WithAppearance(Appearance.Neutral)
                    .WithClickAction(_ =>
                    {
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }))
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(10)
                .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
            .WithVerticalGap(15)
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

        var dialog = Controls.Dialog(editForm, "Edit Task")
            .WithSize("M")
            .WithClosable(false);

        host.UpdateArea(DialogControl.DialogArea, dialog);
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

    private static UiControl BuildNewTaskButton(LayoutAreaHost host, string hubPath)
    {
        return Controls.Button("+ New Task")
            .WithAppearance(Appearance.Accent)
            .WithStyle(style => style.WithMarginBottom("16px"))
            .WithClickAction(_ =>
            {
                OpenCreateTodoDialog(host, hubPath);
                return System.Threading.Tasks.Task.CompletedTask;
            });
    }

    private static void OpenCreateTodoDialog(LayoutAreaHost host, string hubPath)
    {
        var newId = Guid.NewGuid().ToString("N").Substring(0, 8);

        // Create a temporary Todo object to use with host.Edit()
        var newTodo = new Todo
        {
            Id = newId,
            Title = "New Task",
            Status = TodoStatus.Pending,
            Priority = TaskPriority.Medium,
            Category = "General",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var createDataId = $"CreateTodo_{newId}";

        var createForm = Controls.Stack
            .WithView(Controls.H5("Create New Task")
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(host.Edit(newTodo, createDataId)?
                .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), createDataId)
            .WithView(Controls.Stack
                .WithView(Controls.Button("Create")
                    .WithClickAction(_ => HandleCreateTodo(host, hubPath, createDataId)))
                .WithView(Controls.Button("Cancel")
                    .WithClickAction(_ =>
                    {
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }))
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(10)
                .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
            .WithVerticalGap(15)
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

        var dialog = Controls.Dialog(createForm, "Create Task")
            .WithSize("M")
            .WithClosable(false);

        host.UpdateArea(DialogControl.DialogArea, dialog);
    }
}
