using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Todo.Domain;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Todo.LayoutAreas;

/// <summary>
/// Layout areas for the Todo application
/// </summary>
public static class TodoLayoutAreas
{
    /// <summary>
    /// Creates a AllItems layout area that subscribes to a stream of todo items
    /// and displays them in an interactive format with action buttons
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control that updates when todo items change</returns>
    [Display(GroupName = "2. Team Overview", Order = 2)]
    public static IObservable<UiControl?> AllItems(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        // Subscribe to the stream of TodoItem entities from the data source
        return host.Workspace
            .GetStream<TodoItem>()!
            .Select(todoItems => CreateInteractiveTodoListStack(todoItems!, host))
            .StartWith(Controls.Markdown("# Todo List\n\n*Loading todo items...*"));
    }

    /// <summary>
    /// Creates a TodosByCategory layout area that groups todos by category with interactive controls
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control showing todos grouped by category with action buttons</returns>
    [Display(GroupName = "2. Team Overview", Order = 3)]
    public static IObservable<UiControl?> TodosByCategory(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        return host.Workspace
            .GetStream<TodoItem>()!
            .Select(todoItems => CreateInteractiveTodosByCategory(todoItems!, host))
            .StartWith(Controls.Markdown("# 📂 Todos by Category\n\n*Loading todo items...*"));
    }

    /// <summary>
    /// Creates a Summary layout area that shows interactive summary statistics with action buttons
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control showing interactive todo summary statistics</returns>
    [Display(GroupName = "2. Team Overview", Order = 1)]
    public static IObservable<UiControl?> Summary(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        return host.Workspace
            .GetStream<TodoItem>()!
            .Select(todoItems => CreateInteractiveTodoSummary(todoItems!, host))
            .StartWith(Controls.Markdown("# 📊 Todo Summary\n\n*Loading todo statistics...*"));
    }

    /// <summary>
    /// Creates a Planning View for task assignment and workload management
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control showing team workload and assignment interface</returns>
    [Display(GroupName = "3. Planning")]
    public static IObservable<UiControl?> Planning(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        return host.Workspace
            .GetStream<TodoItem>()!
            .Select(todoItems => CreatePlanningView(todoItems!, host))
            .StartWith(Controls.Markdown("# 🎯 Planning & Assignment\n\n*Loading team workload...*"));
    }

    /// <summary>
    /// Creates a view showing only the current user's active tasks
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control showing current user's active tasks</returns>
    [Display(GroupName = "1. My Overview")]
    public static IObservable<UiControl?> MyTasks(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        return host.Workspace
            .GetStream<TodoItem>()!
            .Select(todoItems => CreateMyActiveTasks(todoItems!, host))
            .StartWith(Controls.Markdown("# 🟢 My Active Tasks\n\n*Loading your tasks...*"));
    }

    /// <summary>
    /// Creates a view showing all unassigned tasks
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control showing unassigned tasks</returns>
    [Display(GroupName = "3. Planning")]
    public static IObservable<UiControl?> Backlog(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        return host.Workspace
            .GetStream<TodoItem>()!
            .Select(todoItems => CreateUnassignedTasks(todoItems!, host))
            .StartWith(Controls.Markdown("# 📋 Unassigned Tasks\n\n*Loading unassigned tasks...*"));
    }

    /// <summary>
    /// Creates a Today's Focus view showing all items due today
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control showing today's focus items</returns>
    [Display(GroupName = "2. Team Overview", Order = 0)]
    public static IObservable<UiControl?> TodaysFocus(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        return host.Workspace
            .GetStream<TodoItem>()!
            .Select(todoItems => CreateTodaysFocus(todoItems!, host))
            .StartWith(Controls.Markdown("# 🎯 Today's Focus\n\n*Loading today's priorities...*"));
    }

    /// <summary>
    /// Creates clean summary statistics without dummy buttons
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="_">The layout area host (unused parameter)</param>
    /// <returns>A clean LayoutGrid control with summary statistics only</returns>
    private static UiControl? CreateInteractiveTodoSummary(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost _)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Simple header
        mainGrid = mainGrid
            .WithView(Controls.H4("📊 Todo Dashboard")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        if (!todoItems.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No todo items found.*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Overall statistics
        var totalCount = todoItems.Count;
        mainGrid = mainGrid
            .WithView(Controls.Markdown($"**Total Items:** {totalCount}")
                .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("8px")),
                skin => skin.WithXs(12));

        // Status breakdown
        mainGrid = mainGrid
            .WithView(Controls.H5("📈 Status Overview")
                .WithStyle(style => style.WithMarginTop("12px").WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        var statusGroups = todoItems.GroupBy(t => t.Status);
        foreach (var group in statusGroups.OrderBy(g => (int)g.Key))
        {
            var statusIcon = GetStatusIcon(group.Key);
            var percentage = (group.Count() * 100.0 / totalCount).ToString("F1");

            mainGrid = mainGrid
                .WithView(Controls.Markdown($"{statusIcon} **{group.Key}**: {group.Count()} ({percentage}%)")
                    .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                    skin => skin.WithXs(12));
        }

        // Due date analysis
        var now = DateTime.Now.Date;
        var overdue = todoItems.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date < now && t.Status != TodoStatus.Completed).ToList();
        var dueToday = todoItems.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date == now && t.Status != TodoStatus.Completed).ToList();
        var dueSoon = todoItems.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date > now && t.DueDate.Value.Date <= now.AddDays(7) && t.Status != TodoStatus.Completed).ToList();

        mainGrid = mainGrid
            .WithView(Controls.H5("⏰ Due Date Insights")
                .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"🚨 **Overdue**: {overdue.Count}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"⏰ **Due Today**: {dueToday.Count}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"📅 **Due This Week**: {dueSoon.Count}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        // Responsible person breakdown
        var currentUser = ResponsiblePersons.GetCurrentUser();
        var myTodos = todoItems.Where(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson)).ToList();
        var otherTodos = todoItems.Where(t => !ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson)).ToList();

        var unassignedTodos = todoItems.Where(t => t.ResponsiblePerson == "Unassigned").ToList();

        mainGrid = mainGrid
            .WithView(Controls.H5("🟢 Responsibility Overview")
                .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12));

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"🟢 **My Tasks ({currentUser})**: {myTodos.Count}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"🫂 **Others' Tasks**: {otherTodos.Count}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"📋 **Unassigned Tasks**: {unassignedTodos.Count}")
                .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                skin => skin.WithXs(12));

        return mainGrid;
    }

    /// <summary>
    /// Creates summary statistics for todo items in markdown format (legacy method)
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <returns>A markdown control with summary statistics</returns>
    private static UiControl? CreateTodoSummaryMarkdown(IReadOnlyCollection<TodoItem> todoItems)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 📊 Todo Summary");
        sb.AppendLine();

        if (!todoItems.Any())
        {
            sb.AppendLine("*No todo items found.*");
            return Controls.Markdown(sb.ToString());
        }

        // Overall statistics
        sb.AppendLine($"**Total Items:** {todoItems.Count}");
        sb.AppendLine();

        // Status breakdown
        sb.AppendLine("## Status Breakdown");
        var statusGroups = todoItems.GroupBy(t => t.Status);
        foreach (var group in statusGroups.OrderBy(g => (int)g.Key))
        {
            var statusIcon = GetStatusIcon(group.Key);
            var percentage = (group.Count() * 100.0 / todoItems.Count).ToString("F1");
            sb.AppendLine($"- {statusIcon} **{group.Key}**: {group.Count()} ({percentage}%)");
        }
        sb.AppendLine();

        // Category breakdown
        sb.AppendLine("## Category Breakdown");
        var categoryGroups = todoItems.GroupBy(t => string.IsNullOrEmpty(t.Category) ? "Uncategorized" : t.Category);
        foreach (var group in categoryGroups.OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"- **{group.Key}**: {group.Count()}");
        }
        sb.AppendLine();

        // Due date analysis
        var now = DateTime.Now.Date;
        var overdue = todoItems.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date < now && t.Status != TodoStatus.Completed);
        var dueToday = todoItems.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date == now && t.Status != TodoStatus.Completed);
        var dueSoon = todoItems.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date > now && t.DueDate.Value.Date <= now.AddDays(7) && t.Status != TodoStatus.Completed);

        sb.AppendLine("## Due Date Analysis");
        sb.AppendLine($"- 🚨 **Overdue**: {overdue}");
        sb.AppendLine($"- ⏰ **Due Today**: {dueToday}");
        sb.AppendLine($"- 📅 **Due This Week**: {dueSoon}");
        sb.AppendLine();

        // Responsible person breakdown
        sb.AppendLine("## Responsibility Breakdown");
        var currentUser = ResponsiblePersons.GetCurrentUser();
        var myTodos = todoItems.Where(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson)).ToList();
        var otherTodos = todoItems.Where(t => !ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson)).ToList();
        
        sb.AppendLine($"- 🟢 **My Tasks ({currentUser})**: {myTodos.Count}");
        sb.AppendLine($"- 🫂 **Others' Tasks**: {otherTodos.Count}");
        
        // Show top responsible persons
        var personGroups = todoItems.GroupBy(t => t.ResponsiblePerson)
            .OrderByDescending(g => g.Count())
            .Take(5);
            
        sb.AppendLine();
        sb.AppendLine("## Top Contributors");
        foreach (var group in personGroups)
        {
            var indicator = ResponsiblePersons.GetCurrentUserIndicator(group.Key);
            sb.AppendLine($"- {indicator}**{group.Key}**: {group.Count()}");
        }

        return Controls.Markdown(sb.ToString());
    }

    /// <summary>
    /// Creates interactive todos grouped by category with category-level and item-level actions
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>An interactive LayoutGrid control with categorized todos and actions</returns>
    private static UiControl? CreateInteractiveTodosByCategory(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header with title and global actions
        mainGrid = mainGrid
            .WithView(Controls.H2("📂 Todo Categories")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(Controls.MenuItem("➕ Add New Todo", "plus")
                .WithClickAction(_ => { SubmitNewTodo(host); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => HeadingButtonStyle(style)),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        if (!todoItems.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No todo items found. Click 'Add New Todo' to get started!*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(Controls.Html(""),
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));
            return mainGrid;
        }

        var categoryGroups = todoItems
            .GroupBy(t => string.IsNullOrEmpty(t.Category) ? "Uncategorized" : t.Category)
            .OrderBy(g => g.Key);

        foreach (var categoryGroup in categoryGroups)
        {
            var categoryTodos = categoryGroup.ToList();
            var completedCount = categoryTodos.Count(t => t.Status == TodoStatus.Completed);
            var pendingCount = categoryTodos.Count(t => t.Status == TodoStatus.Pending);
            var inProgressCount = categoryTodos.Count(t => t.Status == TodoStatus.InProgress);

            // Category header with actions
            var categoryActionButton = CreateCategoryActionButton(categoryTodos, host);
            
            mainGrid = mainGrid
                .WithView(Controls.H5($"📁 {categoryGroup.Key} ({categoryGroup.Count()}) - {completedCount}✅ {inProgressCount}🔄 {pendingCount}⏳")
                    .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("8px").WithColor("var(--color-fg-default)")),
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(categoryActionButton,
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));

            // Group todos by status within this category for better organization
            var statusGroups = categoryTodos.GroupBy(t => t.Status).OrderBy(g => (int)g.Key);

            foreach (var statusGroup in statusGroups)
            {
                var sortedTodos = statusGroup
                    .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
                    .ThenBy(t => t.CreatedAt);

                foreach (var todo in sortedTodos)
                {
                    var (todoContent, todoActions) = CreateTodoItemContentAndActions(todo, host);

                    mainGrid = mainGrid
                        .WithView(todoContent,
                            skin => skin.WithXs(12).WithSm(9).WithMd(10))
                        .WithView(todoActions,
                            skin => skin.WithXs(12).WithSm(3).WithMd(2));
                }
            }
        }

        return mainGrid;
    }

    /// <summary>
    /// Creates todos grouped by category in markdown format (legacy method)
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <returns>A markdown control with todos grouped by category</returns>
    private static UiControl? CreateTodosByCategoryMarkdown(IReadOnlyCollection<TodoItem> todoItems)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 📂 Todos by Category");
        sb.AppendLine();

        if (!todoItems.Any())
        {
            sb.AppendLine("*No todo items found.*");
            return Controls.Markdown(sb.ToString());
        }

        var categoryGroups = todoItems
            .GroupBy(t => string.IsNullOrEmpty(t.Category) ? "Uncategorized" : t.Category)
            .OrderBy(g => g.Key);

        foreach (var categoryGroup in categoryGroups)
        {
            sb.AppendLine($"## {categoryGroup.Key} ({categoryGroup.Count()})");
            sb.AppendLine();

            var sortedTodos = categoryGroup
                .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
                .ThenBy(t => t.CreatedAt);

            foreach (var todo in sortedTodos)
            {
                sb.AppendLine(FormatTodoItem(todo));
                sb.AppendLine();
            }
        }

        return Controls.Markdown(sb.ToString());
    }

    /// <summary>
    /// Formats a single todo item for markdown display
    /// </summary>
    /// <param name="todo">The todo item to format</param>
    /// <returns>Formatted markdown string for the todo item</returns>
    private static string FormatTodoItem(TodoItem todo)
    {
        var sb = new StringBuilder();

        var statusIcon = GetStatusIcon(todo.Status);
        sb.AppendLine($"### {statusIcon} {todo.Title}");

        if (!string.IsNullOrEmpty(todo.Description))
        {
            sb.AppendLine($"*{todo.Description}*");
        }

        sb.AppendLine($"**Status:** {todo.Status}");
        sb.AppendLine($"**Category:** {todo.Category}");
        
        var currentUserIndicator = ResponsiblePersons.GetCurrentUserIndicator(todo.ResponsiblePerson);
        sb.AppendLine($"**Responsible:** {currentUserIndicator}{todo.ResponsiblePerson}");

        if (todo.DueDate.HasValue)
        {
            var dueDateStr = todo.DueDate.Value.ToString("yyyy-MM-dd");
            var isOverdue = todo.DueDate.Value.Date < DateTime.Now.Date && todo.Status != TodoStatus.Completed;
            var isDueToday = todo.DueDate.Value.Date == DateTime.Now.Date && todo.Status != TodoStatus.Completed;

            if (isOverdue)
                sb.AppendLine($"**Due Date:** 🚨 {dueDateStr} *(Overdue)*");
            else if (isDueToday)
                sb.AppendLine($"**Due Date:** ⏰ {dueDateStr} *(Due Today)*");
            else
                sb.AppendLine($"**Due Date:** {dueDateStr}");
        }

        sb.AppendLine($"**Created:** {todo.CreatedAt:yyyy-MM-dd HH:mm}");

        if (todo.UpdatedAt != todo.CreatedAt)
        {
            sb.AppendLine($"**Updated:** {todo.UpdatedAt:yyyy-MM-dd HH:mm}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the appropriate icon for a todo status
    /// </summary>
    /// <param name="status">The todo status</param>
    /// <returns>An emoji icon representing the status</returns>
    private static string GetStatusIcon(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "⏳",
        TodoStatus.InProgress => "🔄",
        TodoStatus.Completed => "✅",
        TodoStatus.Cancelled => "❌",
        _ => "❓"
    };

    /// <summary>
    /// Gets the appropriate color for a todo status
    /// </summary>
    /// <param name="status">The todo status</param>
    /// <returns>A CSS color variable for the status</returns>
    private static string GetStatusColor(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "var(--color-warning-fg)",
        TodoStatus.InProgress => "var(--color-accent-fg)",
        TodoStatus.Completed => "var(--color-success-fg)",
        TodoStatus.Cancelled => "var(--color-danger-fg)",
        _ => "var(--color-fg-default)"
    };

    /// <summary>
    /// Creates an interactive todo list with a clean layout grid structure and vertically aligned action buttons
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>A LayoutGrid control with structured todo items</returns>
    private static UiControl? CreateInteractiveTodoListStack(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost host)
    {
        // Create main LayoutGrid with minimal spacing
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // First row: Title and Add New Todo button - smaller heading with reduced spacing
        mainGrid = mainGrid
            .WithView(Controls.H4("📝 Todo List with Actions")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(Controls.MenuItem("➕ Add New Todo", "plus")
                .WithClickAction(_ => { SubmitNewTodo(host); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => HeadingButtonStyle(style)),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        if (!todoItems.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No todo items found. Click 'Add New Todo' to get started!*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(Controls.Html(""),
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));
            return mainGrid;
        }

        // Order by due date (nulls last), then by created date
        var orderedTodos = todoItems
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        // Group by status for better organization, but separate unassigned tasks
        var assignedTodos = orderedTodos.Where(t => t.ResponsiblePerson != "Unassigned").ToList();
        var unassignedTodos = orderedTodos.Where(t => t.ResponsiblePerson == "Unassigned").ToList();

        var assignedStatusGroups = assignedTodos.GroupBy(t => t.Status).ToList();
        var unassignedStatusGroups = unassignedTodos.GroupBy(t => t.Status).ToList();

        // First show assigned todos in collapsible status sections
        foreach (var statusGroup in assignedStatusGroups.OrderBy(g => (int)g.Key))
        {
            var statusIcon = GetStatusIcon(statusGroup.Key);
            var statusName = statusGroup.Key.ToString();
            var statusColor = GetStatusColor(statusGroup.Key);
            var defaultOpen = statusGroup.Key == TodoStatus.Pending || statusGroup.Key == TodoStatus.InProgress;

            // Create hierarchical action menu for status header
            var statusActionButton = statusGroup.Key switch
            {
                TodoStatus.Pending => CreateStatusGroupMenuItem("▶️ Start All", "play",
                    (host, todos) => UpdateAllTodosInGroup(host, todos, TodoStatus.InProgress),
                    host, statusGroup),
                TodoStatus.InProgress => CreateStatusGroupMenuItem("⏸️ Close All", "pause",
                    (host, todos) => UpdateAllTodosInGroup(host, todos, TodoStatus.Completed),
                    host, statusGroup),
                TodoStatus.Completed => CreateStatusGroupMenuItem("📦 Archive All", "archive",
                    DeleteAllTodosInGroup,
                    host, statusGroup),
                TodoStatus.Cancelled => CreateStatusGroupMenuItem("🗑️ Delete All", "trash",
                    DeleteAllTodosInGroup,
                    host, statusGroup),
                _ => Controls.Html("") // Empty placeholder for other statuses
            };

            mainGrid = AddCollapsibleStatusSection(mainGrid, host, statusGroup.ToList(), 
                $"{statusIcon} {statusName}", statusColor, statusActionButton, defaultOpen);
        }

        // Then show unassigned todos after all assigned todos
        if (unassignedTodos.Any())
        {
            foreach (var statusGroup in unassignedStatusGroups.OrderBy(g => (int)g.Key))
            {
                var statusIcon = GetStatusIcon(statusGroup.Key);
                var statusName = $"Unassigned {statusGroup.Key}";

                // Create action button for unassigned group - focus on assignment
                var assignmentActionButton = CreateStatusGroupMenuItem("👥 Auto-Assign All", "user-plus",
                    (host, todos) => AutoAssignTasks(host, todos.ToList()),
                    host, statusGroup);

                mainGrid = AddCollapsibleUnassignedSection(mainGrid, host, statusGroup.ToList(), 
                    $"{statusIcon} {statusName}", "var(--color-warning-fg)", assignmentActionButton, true);
            }
        }

        return mainGrid;
    }


    private const string MenuWidth = "150px";
    /// <summary>
    /// Creates hierarchical menu item action controls with primary action and expandable secondary actions
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>A hierarchical menu item control with actions</returns>
    private static UiControl CreateActionControls(TodoItem todo, LayoutAreaHost host)
    {
        var primaryAction = GetPrimaryActionData(todo);
        var menuItem = Controls.MenuItem(primaryAction.Title, primaryAction.Icon)
            .WithClickAction(_ => { primaryAction.Action(host, todo); return Task.CompletedTask; })
            .WithWidth(MenuWidth)
            .WithAppearance(Appearance.Neutral)
            .WithStyle(style => HeadingButtonStyle(style));

        // Add secondary actions as sub-views
        var secondaryActions = GetSecondaryActionData(todo);
        foreach (var action in secondaryActions)
        {
            var subMenuItem = Controls.MenuItem(action.Title, action.Icon)
                .WithClickAction(_ => { action.Action(host, todo); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => HeadingButtonStyle(style));
            menuItem = menuItem.WithView(subMenuItem);
        }

        // Add overdue reminder button if task is overdue
        if (todo.DueDate.HasValue && todo.DueDate.Value.Date < DateTime.Now.Date && 
            todo.Status != TodoStatus.Completed && todo.Status != TodoStatus.Cancelled)
        {
            var reminderButton = CreateOverdueReminderButton(todo, host);
            menuItem = menuItem.WithView(reminderButton);
        }

        return menuItem;
    }



    /// <summary>
    /// Updates the status of a todo item
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="todo">The todo item to update</param>
    /// <param name="newStatus">The new status</param>
    private static void UpdateTodoStatus(LayoutAreaHost host, TodoItem todo, TodoStatus newStatus)
    {
        var updatedTodo = todo with
        {
            Status = newStatus,
            UpdatedAt = DateTime.UtcNow
        };
        SubmitTodoUpdate(host, updatedTodo);
    }

    /// <summary>
    /// Submits an update request for a todo item
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="updatedTodo">The updated todo item</param>
    private static void SubmitTodoUpdate(LayoutAreaHost host, TodoItem updatedTodo)
    {
        var changeRequest = new DataChangeRequest()
            .WithUpdates(updatedTodo);

        host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));
    }

    /// <summary>
    /// Submits a delete request for a todo item
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="todoToDelete">The todo item to delete</param>
    private static void SubmitTodoDelete(LayoutAreaHost host, TodoItem todoToDelete)
    {
        var changeRequest = new DataChangeRequest()
            .WithDeletions(todoToDelete);

        host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));
    }

    /// <summary>
    /// Submits a request to create a new todo item using a dialog
    /// </summary>
    /// <param name="host">The layout area host</param>
    private static void SubmitNewTodo(LayoutAreaHost host)
    {
        // Create a new empty todo item for editing
        var newTodo = new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "",
            Description = "",
            Status = TodoStatus.Pending,
            Category = "General",
            ResponsiblePerson = ResponsiblePersons.GetCurrentUser(), // Default to current user
            DueDate = DateTime.Now.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Define the area ID for the new todo data
        const string newTodoDataId = "NewTodoData";

        // Create an edit form for the new todo item with proper data binding
        var editForm = Controls.Stack
            .WithView(Controls.H5("Create New Todo")
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(host.Edit(newTodo, newTodoDataId)?
                .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), newTodoDataId)
            .WithView(Controls.Stack
                .WithView(Controls.Button("💾 Save Todo")
                    .WithClickAction(_ =>
                    {
                        // Changes are saved immediately ==> just
                        // Close the dialog by clearing the dialog area
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button("❌ Cancel")
                    .WithClickAction(_ =>
                    {
                        // since we have saved immediately, we need to now delete the entity.
                        host.Hub.Post(new DataChangeRequest() { Deletions = [newTodo] }, o => o.WithTarget(TodoApplicationAttribute.Address));

                        // Close the dialog by clearing the dialog area
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return Task.CompletedTask;
                    }))
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(10)
                .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
            .WithVerticalGap(15)
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

        // Create a dialog with the edit form content - DialogControl.Render() will handle the UiControl rendering
        var dialog = Controls.Dialog(editForm, "Create New Todo")
            .WithSize("M")
            .WithClosable(false); // Disable the X button - only Save/Cancel buttons should close the dialog

        // Update the dialog area to show the dialog
        host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    /// <summary>
    /// Updates all todos in a status group to a new status
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="todos">The todos to update</param>
    /// <param name="newStatus">The new status</param>
    private static void UpdateAllTodosInGroup(LayoutAreaHost host, IEnumerable<TodoItem> todos, TodoStatus newStatus)
    {
        var updatedTodos = todos.Select(todo => todo with
        {
            Status = newStatus,
            UpdatedAt = DateTime.UtcNow
        });

        var changeRequest = new DataChangeRequest()
            .WithUpdates(updatedTodos);

        host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));
    }

    /// <summary>
    /// Deletes all todos in a group
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="todos">The todos to delete</param>
    private static void DeleteAllTodosInGroup(LayoutAreaHost host, IEnumerable<TodoItem> todos)
    {
        var changeRequest = new DataChangeRequest()
            .WithDeletions(todos.ToArray());

        host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));
    }

    /// <summary>
    /// Creates compact content for a todo item (single line with key details)
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <returns>Formatted content string</returns>
    private static string CreateCompactTodoContent(TodoItem todo)
    {
        var sb = new StringBuilder();

        // Title with emphasis
        sb.Append($"**{todo.Title}**");

        // Category badge if not default
        if (todo.Category != "General")
        {
            sb.Append($" `{todo.Category}`");
        }

        // Responsible person with current user indicator
        var displayName = ResponsiblePersons.GetDisplayName(todo.ResponsiblePerson);
        sb.Append($" {displayName}");

        // Due date with urgency indicators
        if (todo.DueDate.HasValue)
        {
            var dueDateStr = todo.DueDate.Value.ToString("MMM dd");
            var isOverdue = todo.DueDate.Value.Date < DateTime.Now.Date && todo.Status != TodoStatus.Completed;
            var isDueToday = todo.DueDate.Value.Date == DateTime.Now.Date && todo.Status != TodoStatus.Completed;

            if (isOverdue)
                sb.Append($" 🚨 *{dueDateStr} (Overdue)*");
            else if (isDueToday)
                sb.Append($" ⏰ *Due {dueDateStr}*");
            else
                sb.Append($" 📅 *{dueDateStr}*");
        }

        // Description on new line if present
        if (!string.IsNullOrEmpty(todo.Description))
        {
            sb.AppendLine();
            sb.Append($"*{todo.Description}*");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Creates todo item content and actions as separate controls for layout grid
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>A tuple containing the content control and actions control</returns>
    private static (UiControl content, UiControl actions) CreateTodoItemContentAndActions(TodoItem todo, LayoutAreaHost host)
    {
        var statusIcon = GetStatusIcon(todo.Status);
        var contentMarkdown = CreateCompactTodoContent(todo);
        var actionControls = CreateActionControls(todo, host);

        // Create content control with icon and text - more compact design
        var content = Controls.Stack
            .WithView(
                Controls.Stack
                    .WithView(Controls.Markdown($"{statusIcon}"))
                    .WithStyle(style => style
                        .WithWidth("32px")
                        .WithTextAlign("center")
                        .WithFlexShrink("0")))
            .WithView(
                Controls.Stack
                    .WithView(Controls.Markdown(contentMarkdown))
                    .WithStyle(style => style
                        .WithFlexGrow("1")
                        .WithMinWidth("0")
                        .WithPaddingLeft("12px")))
            .WithStyle(style => style
                .WithDisplay("flex")
                .WithFlexDirection("row")
                .WithAlignItems("flex-start")
                .WithPadding("12px")
                .WithMarginBottom("8px")
                .WithBorder("1px solid var(--color-border-default)")
                .WithBorderRadius("6px")
                .WithBackgroundColor("var(--color-canvas-subtle)")
                .WithBoxShadow("0 1px 2px var(--color-shadow-small)"));

        // Create actions control - centered and compact
        var actions = Controls.Stack
            .WithView(actionControls)
            .WithStyle(style => style
                .WithDisplay("flex")
                .WithJustifyContent("center")
                .WithAlignItems("center")
                .WithPadding("12px")
                .WithMarginBottom("8px"));

        return (content, actions);
    }

    /// <summary>
    /// Represents an action with its display information and handler
    /// </summary>
    /// <param name="Title">The display title for the action</param>
    /// <param name="Icon">The icon for the action</param>
    /// <param name="Action">The action handler</param>
    private record ActionData(string Title, string Icon, Action<LayoutAreaHost, TodoItem> Action);

    /// <summary>
    /// Gets the primary action data for a todo item
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <returns>Primary action data</returns>
    private static ActionData GetPrimaryActionData(TodoItem todo)
    {
        return todo.Status switch
        {
            TodoStatus.Pending => new ActionData("▶️ Start", "play", (host, item) => UpdateTodoStatus(host, item, TodoStatus.InProgress)),
            TodoStatus.InProgress => new ActionData("✅ Done", "check", (host, item) => UpdateTodoStatus(host, item, TodoStatus.Completed)),
            TodoStatus.Completed => new ActionData("🔄 Reopen", "refresh", (host, item) => UpdateTodoStatus(host, item, TodoStatus.InProgress)),
            TodoStatus.Cancelled => new ActionData("🔄 Restore", "refresh", (host, item) => UpdateTodoStatus(host, item, TodoStatus.Pending)),
            _ => new ActionData("❓ Unknown", "help", (host, item) => { })
        };
    }

    /// <summary>
    /// Gets the secondary action data for a todo item
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <returns>List of secondary action data</returns>
    private static List<ActionData> GetSecondaryActionData(TodoItem todo)
    {
        var actions = new List<ActionData>();

        // Always add Edit action
        actions.Add(new ActionData("✏️ Edit", "edit", (host, item) => SubmitEditTodo(host, item)));

        // Status-specific secondary actions
        switch (todo.Status)
        {
            case TodoStatus.Pending:
                actions.Add(new ActionData("✅ Complete", "check", (host, item) => UpdateTodoStatus(host, item, TodoStatus.Completed)));
                actions.Add(new ActionData("❌ Cancel", "close", (host, item) => UpdateTodoStatus(host, item, TodoStatus.Cancelled)));
                break;
            case TodoStatus.InProgress:
                actions.Add(new ActionData("⏸️ Pause", "pause", (host, item) => UpdateTodoStatus(host, item, TodoStatus.Pending)));
                actions.Add(new ActionData("❌ Cancel", "close", (host, item) => UpdateTodoStatus(host, item, TodoStatus.Cancelled)));
                break;
            case TodoStatus.Completed:
            case TodoStatus.Cancelled:
                // No additional status actions for completed/cancelled items
                break;
        }

        // Always add Delete action
        actions.Add(new ActionData("🗑️ Delete", "trash", (host, item) => SubmitTodoDelete(host, item)));

        return actions;
    }

    /// <summary>
    /// Submits a request to edit an existing todo item using a dialog
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="todoToEdit">The todo item to edit</param>
    private static void SubmitEditTodo(LayoutAreaHost host, TodoItem todoToEdit)
    {
        // Define the area ID for the edit todo data
        var editTodoDataId = $"EditTodoData_{todoToEdit.Id}";

        // Capture the original todo item in a closure for cancel functionality
        var originalTodo = todoToEdit;

        // Create an edit form for the todo item with proper data binding
        var editForm = Controls.Stack
            .WithView(Controls.H5("Edit Todo")
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(host.Edit(todoToEdit, editTodoDataId)?
                .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), editTodoDataId)
            .WithView(Controls.Stack
                .WithView(Controls.Button("💾 Done")
                    .WithClickAction(_ =>
                    {
                        // is updated on the fly, so we just need to close the dialog
                        // Close the dialog by clearing the dialog area
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button("❌ Cancel")
                    .WithClickAction(_ =>
                    {
                        // Revert to original todo state
                        var changeRequest = new DataChangeRequest()
                            .WithUpdates(originalTodo);

                        host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));

                        // Close the dialog by clearing the dialog area
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return Task.CompletedTask;
                    }))
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(10)
                .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
            .WithVerticalGap(15)
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

        // Create a dialog with the edit form content
        var dialog = Controls.Dialog(editForm, "Edit Todo")
            .WithSize("M")
            .WithClosable(false); // Disable the X button - only Save/Cancel buttons should close the dialog

        // Update the dialog area to show the dialog
        host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    /// <summary>
    /// Creates a hierarchical menu item for status group operations
    /// </summary>
    /// <param name="title">The primary action title</param>
    /// <param name="icon">The primary action icon</param>
    /// <param name="primaryAction">The primary action to execute</param>
    /// <param name="host">The layout area host</param>
    /// <param name="statusGroup">The group of todos</param>
    /// <returns>A hierarchical menu item with group actions</returns>
    private static UiControl CreateStatusGroupMenuItem(string title, string icon,
        Action<LayoutAreaHost, IEnumerable<TodoItem>> primaryAction,
        LayoutAreaHost host, IGrouping<TodoStatus, TodoItem> statusGroup)
    {
        // Create simple button with only primary action - no submenus for headings
        var menuItem = Controls.MenuItem(title, icon)
            .WithClickAction(_ => { primaryAction(host, statusGroup); return Task.CompletedTask; })
            .WithWidth(MenuWidth)
            .WithAppearance(Appearance.Neutral)
            .WithStyle(style => HeadingButtonStyle(style));

        return menuItem;
    }

    private static readonly Func<StyleBuilder, StyleBuilder> HeadingButtonStyle = style =>
        style.WithDisplay("flex").WithAlignItems("center").WithJustifyContent("flex-end").WithHeight("100%")
            .WithPaddingTop("20px");

    /// <summary>
    /// Creates the Planning View showing team workload and assignment interface
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>An interactive LayoutGrid control for task planning</returns>
    private static UiControl? CreatePlanningView(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost host)
    {
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header
        mainGrid = mainGrid
            .WithView(Controls.H2("🎯 Task Planning & Assignment")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(Controls.MenuItem("➕ Add New Todo", "plus")
                .WithClickAction(_ => { SubmitNewTodo(host); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => HeadingButtonStyle(style)),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        if (!todoItems.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No tasks found. Create some tasks to start planning!*")
                    .WithStyle(style => style.WithColor("var(--color-fg-muted)")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Team workload overview
        var teamWorkload = todoItems
            .Where(t => t.ResponsiblePerson != "Unassigned")
            .GroupBy(t => t.ResponsiblePerson)
            .OrderByDescending(g => g.Count());

        var unassignedTasks = todoItems.Where(t => t.ResponsiblePerson == "Unassigned").ToList();

        // Team Workload Section
        mainGrid = mainGrid
            .WithView(Controls.H5($"👥 Team Workload ({todoItems.Count(t => t.ResponsiblePerson != "Unassigned")} assigned)")
                .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("8px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(Controls.Html(""),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        foreach (var personGroup in teamWorkload.Take(6)) // Show top 6 team members
        {
            var activeCount = personGroup.Count(t => t.Status != TodoStatus.Completed && t.Status != TodoStatus.Cancelled);
            var overdueCount = personGroup.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date < DateTime.Now.Date && t.Status != TodoStatus.Completed);
            var isCurrentUser = ResponsiblePersons.IsCurrentUser(personGroup.Key);
            var displayName = ResponsiblePersons.GetDisplayName(personGroup.Key);
            
            var workloadColor = activeCount switch
            {
                <= 2 => "🟢", // Light workload
                <= 4 => "🟡", // Medium workload  
                _ => "🔴"      // Heavy workload
            };

            var assignButton = unassignedTasks.Any()
                ? Controls.MenuItem("📥 Assign", "user-plus")
                    .WithClickAction(_ => { 
                        // Assign the first unassigned task to this person
                        var taskToAssign = unassignedTasks.First();
                        var updatedTask = taskToAssign with { 
                            ResponsiblePerson = personGroup.Key,
                            UpdatedAt = DateTime.UtcNow 
                        };
                        SubmitTodoUpdate(host, updatedTask);
                        return Task.CompletedTask;
                    })
                    .WithWidth(MenuWidth)
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(style => style.WithDisplay("flex").WithAlignItems("center").WithJustifyContent("flex-end"))
                : (UiControl)Controls.Html("");

            mainGrid = mainGrid
                .WithView(Controls.Markdown($"{workloadColor} **{displayName}**: {activeCount} active" + 
                    (overdueCount > 0 ? $" 🚨 {overdueCount} overdue" : ""))
                    .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(assignButton,
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));
        }

        // Unassigned Tasks Section
        if (unassignedTasks.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.H5($"📋 Unassigned Tasks ({unassignedTasks.Count})")
                    .WithStyle(style => style.WithMarginTop("16px").WithMarginBottom("8px").WithColor("var(--color-fg-default)")),
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(unassignedTasks.Any(t => t.Status != TodoStatus.Completed)
                    ? Controls.MenuItem("🎯 Auto-Assign", "shuffle")
                        .WithClickAction(_ => { 
                            AutoAssignTasks(host, unassignedTasks.Where(t => t.Status != TodoStatus.Completed));
                            return Task.CompletedTask; 
                        })
                        .WithWidth(MenuWidth)
                        .WithAppearance(Appearance.Neutral)
                        .WithStyle(style => HeadingButtonStyle(style))
                    : (UiControl)Controls.Html(""),
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));

            foreach (var task in unassignedTasks.Where(t => t.Status != TodoStatus.Completed).OrderBy(t => t.DueDate ?? DateTime.MaxValue).Take(8))
            {
                var urgencyIndicator = task.DueDate.HasValue && task.DueDate.Value.Date <= DateTime.Now.Date ? "🚨" : 
                                     task.DueDate.HasValue && task.DueDate.Value.Date <= DateTime.Now.Date.AddDays(1) ? "⏰" : "📅";
                
                var assignmentButton = CreateTaskAssignmentButton(task, host);

                mainGrid = mainGrid
                    .WithView(Controls.Markdown($"{urgencyIndicator} **{task.Title}** - `{task.Category}`" + 
                        (task.DueDate.HasValue ? $" *Due: {task.DueDate.Value:MMM dd}*" : ""))
                        .WithStyle(style => style.WithPaddingLeft("20px").WithMarginBottom("5px")),
                        skin => skin.WithXs(12).WithSm(9).WithMd(10))
                    .WithView(assignmentButton,
                        skin => skin.WithXs(12).WithSm(3).WithMd(2));
            }
        }

        return mainGrid;
    }

    /// <summary>
    /// Creates the My Active Tasks view for personal productivity
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>An interactive control showing current user's active tasks</returns>
    private static UiControl? CreateMyActiveTasks(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost host)
    {
        var currentUser = ResponsiblePersons.GetCurrentUser();
        var myActiveTasks = todoItems
            .Where(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson) && 
                       (t.Status == TodoStatus.Pending || t.Status == TodoStatus.InProgress))
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header
        mainGrid = mainGrid
            .WithView(Controls.H2($"🟢 My Active Tasks ({currentUser})")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(Controls.MenuItem("➕ Add New Todo", "plus")
                .WithClickAction(_ => { SubmitNewTodo(host); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => HeadingButtonStyle(style)),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        if (!myActiveTasks.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("🎉 **All caught up!** You have no active tasks assigned.")
                    .WithStyle(style => style.WithColor("var(--color-success-fg)").WithTextAlign("center").WithMarginTop("40px")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Summary stats
        var overdueCount = myActiveTasks.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date < DateTime.Now.Date);
        var dueTodayCount = myActiveTasks.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Now.Date);
        var inProgressCount = myActiveTasks.Count(t => t.Status == TodoStatus.InProgress);

        mainGrid = mainGrid
            .WithView(Controls.Markdown($"**{myActiveTasks.Count} active tasks** • {inProgressCount} in progress" +
                (overdueCount > 0 ? $" • 🚨 {overdueCount} overdue" : "") +
                (dueTodayCount > 0 ? $" • ⏰ {dueTodayCount} due today" : ""))
                .WithStyle(style => style.WithMarginBottom("12px")),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(myActiveTasks.Any(t => t.Status == TodoStatus.Pending)
                ? Controls.MenuItem("▶️ Start All", "play-circle")
                    .WithClickAction(_ => { 
                        var pendingTasks = myActiveTasks.Where(t => t.Status == TodoStatus.Pending);
                        UpdateAllTodosInGroup(host, pendingTasks, TodoStatus.InProgress);
                        return Task.CompletedTask; 
                    })
                    .WithWidth(MenuWidth)
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(style => HeadingButtonStyle(style))
                : (UiControl)Controls.Html(""),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        // Tasks organized by urgency
        var urgentTasks = myActiveTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date <= DateTime.Now.Date).ToList();
        var todayTasks = myActiveTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Now.Date.AddDays(1)).ToList();
        var upcomingTasks = myActiveTasks.Where(t => !t.DueDate.HasValue || t.DueDate.Value.Date > DateTime.Now.Date.AddDays(1)).ToList();

        if (urgentTasks.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.H5("🚨 Urgent (Overdue/Due Today)")
                    .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("6px").WithColor("var(--color-danger-fg)")),
                    skin => skin.WithXs(12));

            foreach (var task in urgentTasks)
            {
                var (todoContent, todoActions) = CreateTodoItemContentAndActions(task, host);
                mainGrid = mainGrid
                    .WithView(todoContent, skin => skin.WithXs(12).WithSm(9).WithMd(10))
                    .WithView(todoActions, skin => skin.WithXs(12).WithSm(3).WithMd(2));
            }
        }

        if (todayTasks.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.H5("⏰ Due Tomorrow")
                    .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("6px").WithColor("var(--color-warning-fg)")),
                    skin => skin.WithXs(12));

            foreach (var task in todayTasks)
            {
                var (todoContent, todoActions) = CreateTodoItemContentAndActions(task, host);
                mainGrid = mainGrid
                    .WithView(todoContent, skin => skin.WithXs(12).WithSm(9).WithMd(10))
                    .WithView(todoActions, skin => skin.WithXs(12).WithSm(3).WithMd(2));
            }
        }

        if (upcomingTasks.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.H5("📅 Upcoming")
                    .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                    skin => skin.WithXs(12));

            foreach (var task in upcomingTasks.Take(10)) // Limit to 10 upcoming tasks
            {
                var (todoContent, todoActions) = CreateTodoItemContentAndActions(task, host);
                mainGrid = mainGrid
                    .WithView(todoContent, skin => skin.WithXs(12).WithSm(9).WithMd(10))
                    .WithView(todoActions, skin => skin.WithXs(12).WithSm(3).WithMd(2));
            }
        }

        return mainGrid;
    }

    /// <summary>
    /// Creates the Unassigned Tasks view for task assignment
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>An interactive control for managing unassigned tasks</returns>
    private static UiControl? CreateUnassignedTasks(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost host)
    {
        var unassignedTasks = todoItems
            .Where(t => t.ResponsiblePerson == "Unassigned" && t.Status != TodoStatus.Completed && t.Status != TodoStatus.Cancelled)
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header
        mainGrid = mainGrid
            .WithView(Controls.H2($"📋 Unassigned Tasks ({unassignedTasks.Count})")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(unassignedTasks.Any()
                ? Controls.MenuItem("🎯 Auto-Assign All", "shuffle")
                    .WithClickAction(_ => { 
                        AutoAssignTasks(host, unassignedTasks);
                        return Task.CompletedTask; 
                    })
                    .WithWidth(MenuWidth)
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(style => HeadingButtonStyle(style))
                : Controls.MenuItem("➕ Add New Todo", "plus")
                    .WithClickAction(_ => { SubmitNewTodo(host); return Task.CompletedTask; })
                    .WithWidth(MenuWidth)
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(style => HeadingButtonStyle(style)),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        if (!unassignedTasks.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("🎉 **All tasks are assigned!** No unassigned tasks found.")
                    .WithStyle(style => style.WithColor("var(--color-success-fg)").WithTextAlign("center").WithMarginTop("40px")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Show unassigned tasks with assignment buttons
        foreach (var task in unassignedTasks)
        {
            var urgencyIndicator = task.DueDate.HasValue && task.DueDate.Value.Date < DateTime.Now.Date ? "🚨" : 
                                 task.DueDate.HasValue && task.DueDate.Value.Date <= DateTime.Now.Date.AddDays(1) ? "⏰" : "📅";

            var (todoContent, _) = CreateTodoItemContentAndActions(task, host);
            var assignmentButton = CreateTaskAssignmentButton(task, host);

            mainGrid = mainGrid
                .WithView(todoContent,
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(assignmentButton,
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));
        }

        return mainGrid;
    }

    /// <summary>
    /// Creates Today's Focus view showing items due today
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>An interactive control for today's priorities</returns>
    private static UiControl? CreateTodaysFocus(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost host)
    {
        var today = DateTime.Now.Date;
        
        // Categorize tasks by timeline
        var overdueTasks = todoItems
            .Where(t => t.DueDate.HasValue && t.DueDate.Value.Date < today && 
                       t.Status != TodoStatus.Completed && t.Status != TodoStatus.Cancelled)
            .OrderBy(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson) ? 0 : 1)
            .ThenBy(t => t.Status)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var dueTodayTasks = todoItems
            .Where(t => t.DueDate.HasValue && t.DueDate.Value.Date == today && 
                       t.Status != TodoStatus.Completed && t.Status != TodoStatus.Cancelled)
            .OrderBy(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson) ? 0 : 1)
            .ThenBy(t => t.Status)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var ongoingTasks = todoItems
            .Where(t => t.ResponsiblePerson != "Unassigned" &&
                       t.Status != TodoStatus.Completed && t.Status != TodoStatus.Cancelled &&
                       (!t.DueDate.HasValue || t.DueDate.Value.Date > today)) // Only future or no due date
            .OrderBy(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson) ? 0 : 1)
            .ThenBy(t => t.Status)
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var totalUrgentTasks = overdueTasks.Count + dueTodayTasks.Count;
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header with overall summary
        var myUrgentTasks = overdueTasks.Concat(dueTodayTasks).Count(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson));
        var teamUrgentTasks = totalUrgentTasks - myUrgentTasks;
        var myOngoingTasks = ongoingTasks.Count(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson));

        var summaryText = $"**Overdue**: {overdueTasks.Count} • **Due today**: {dueTodayTasks.Count} • **Ongoing**: {ongoingTasks.Count}";

        mainGrid = mainGrid
            .WithView(Controls.H4("🎯 Today's Focus")
                .WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => skin.WithXs(12))
            .WithView(Controls.Markdown(summaryText)
                .WithStyle(style => style.WithMarginBottom("12px")),
                skin => skin.WithXs(12));

        if (totalUrgentTasks == 0 && ongoingTasks.Count == 0)
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("🎉 **All caught up!** No urgent tasks or active work.")
                    .WithStyle(style => style.WithColor("var(--color-success-fg)").WithTextAlign("center").WithMarginTop("40px")),
                    skin => skin.WithXs(12));
            return mainGrid;
        }

        // Section 1: 🚨 Overdue Tasks (highest priority)
        if (overdueTasks.Any())
        {
            mainGrid = AddTimelineSection(mainGrid, host, overdueTasks, 
                "🚨 Overdue", "var(--color-danger-fg)", 
                "▶️ Start All Overdue", "Start all overdue tasks");
        }

        // Section 2: ⏰ Due Today Tasks  
        if (dueTodayTasks.Any())
        {
            mainGrid = AddTimelineSection(mainGrid, host, dueTodayTasks,
                "⏰ Due Today", "var(--color-warning-fg)",
                "▶️ Start All Today", "Start all due today tasks");
        }

        // Section 3: 📋 Ongoing Tasks (all assigned work not urgent)
        if (ongoingTasks.Any())
        {
            mainGrid = AddTimelineSection(mainGrid, host, ongoingTasks,
                "📋 Ongoing", "var(--color-accent-fg)",
                "✅ Complete All", "Complete all ongoing tasks");
        }

        return mainGrid;
    }

    /// <summary>
    /// Adds a timeline section to the Today's Focus view
    /// </summary>
    private static LayoutGridControl AddTimelineSection(LayoutGridControl mainGrid, LayoutAreaHost host, List<TodoItem> tasks, 
        string sectionTitle, string sectionColor, string actionText, string actionDescription)
    {
        // Section header with action button - smaller heading with reduced spacing
        mainGrid = mainGrid
            .WithView(Controls.H5($"{sectionTitle} ({tasks.Count})")
                .WithStyle(style => style.WithMarginTop("12px").WithMarginBottom("6px").WithColor(sectionColor)),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(Controls.MenuItem(actionText, GetActionIcon(actionText))
                .WithClickAction(_ => { 
                    var targetStatus = actionText.Contains("Complete") ? TodoStatus.Completed : TodoStatus.InProgress;
                    UpdateAllTodosInGroup(host, tasks, targetStatus);
                    return Task.CompletedTask; 
                })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => HeadingButtonStyle(style)),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        // Show all tasks directly without person grouping headers
        foreach (var task in tasks)
        {
            var (todoContent, todoActions) = CreateTodoItemContentAndActions(task, host);
            mainGrid = mainGrid
                .WithView(todoContent, skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(todoActions, skin => skin.WithXs(12).WithSm(3).WithMd(2));
        }

        return mainGrid;
    }


    /// <summary>
    /// Adds a collapsible status section for assigned todos in the AllItems
    /// </summary>
    private static LayoutGridControl AddCollapsibleStatusSection(LayoutGridControl mainGrid, LayoutAreaHost host, List<TodoItem> tasks, 
        string sectionTitle, string sectionColor, UiControl actionButton, bool defaultOpen)
    {
        // Add collapsible section header with action button
        var summaryHtml = $"<details{(defaultOpen ? " open" : "")} style=\"margin-top: 20px;\" id=\"status-{sectionTitle.Replace(" ", "-").ToLower()}\">" +
                         $"<summary style=\"cursor: pointer; font-weight: bold; color: {sectionColor}; margin-bottom: 10px; font-size: 1.1em; list-style-position: outside;\">" +
                         $"{sectionTitle} ({tasks.Count})" +
                         "</summary>" +
                         "<div style=\"margin-left: 10px;\">";

        mainGrid = mainGrid
            .WithView(Controls.Html(summaryHtml),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(actionButton,
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        // Add each task with its action buttons inside the collapsible section
        foreach (var task in tasks)
        {
            var (todoContent, todoActions) = CreateTodoItemContentAndActions(task, host);
            mainGrid = mainGrid
                .WithView(todoContent.WithStyle(style => style.WithMarginLeft("10px")), 
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(todoActions, skin => skin.WithXs(12).WithSm(3).WithMd(2));
        }

        // Close the details section
        mainGrid = mainGrid
            .WithView(Controls.Html("</div></details>"),
                skin => skin.WithXs(12));

        return mainGrid;
    }

    /// <summary>
    /// Adds a collapsible status section for unassigned todos in the AllItems
    /// </summary>
    private static LayoutGridControl AddCollapsibleUnassignedSection(LayoutGridControl mainGrid, LayoutAreaHost host, List<TodoItem> tasks, 
        string sectionTitle, string sectionColor, UiControl actionButton, bool defaultOpen)
    {
        // Add collapsible section header with action button
        var summaryHtml = $"<details{(defaultOpen ? " open" : "")} style=\"margin-top: 20px;\" id=\"unassigned-{sectionTitle.Replace(" ", "-").ToLower()}\">" +
                         $"<summary style=\"cursor: pointer; font-weight: bold; color: {sectionColor}; margin-bottom: 10px; font-size: 1.1em; list-style-position: outside;\">" +
                         $"{sectionTitle} ({tasks.Count})" +
                         "</summary>" +
                         "<div style=\"margin-left: 10px;\">";

        mainGrid = mainGrid
            .WithView(Controls.Html(summaryHtml),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(actionButton,
                skin => skin.WithXs(12).WithSm(3).WithMd(2));

        // Add each unassigned task with assignment button
        foreach (var task in tasks)
        {
            var (todoContent, _) = CreateTodoItemContentAndActions(task, host);
            var assignmentButton = CreateTaskAssignmentButton(task, host);

            mainGrid = mainGrid
                .WithView(todoContent.WithStyle(style => style.WithMarginLeft("10px")), 
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(Controls.Stack
                    .WithView(assignmentButton)
                    .WithStyle(style => style
                        .WithDisplay("flex")
                        .WithJustifyContent("center")
                        .WithAlignItems("center")
                        .WithPadding("12px")
                        .WithMarginBottom("8px")),
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));
        }

        // Close the details section
        mainGrid = mainGrid
            .WithView(Controls.Html("</div></details>"),
                skin => skin.WithXs(12));

        return mainGrid;
    }

    /// <summary>
    /// Gets the appropriate icon for an action text
    /// </summary>
    private static string GetActionIcon(string actionText)
    {
        return actionText.Contains("Complete") ? "check" : "play";
    }

    /// <summary>
    /// Creates a task assignment button with dropdown for team members
    /// </summary>
    /// <param name="task">The task to assign</param>
    /// <param name="host">The layout area host</param>
    /// <returns>A menu item with assignment options</returns>
    private static UiControl CreateTaskAssignmentButton(TodoItem task, LayoutAreaHost host)
    {
        var assignButton = Controls.MenuItem("👥 Assign", "user-plus")
            .WithWidth(MenuWidth)
            .WithAppearance(Appearance.Neutral)
            .WithStyle(style => HeadingButtonStyle(style));

        // Add assignment options for each team member
        foreach (var person in ResponsiblePersons.AvailablePersons.Take(5)) // Show top 5 for space
        {
            var displayName = ResponsiblePersons.GetDisplayName(person);
            var assignToPerson = Controls.MenuItem(displayName, "user")
                .WithClickAction(_ => {
                    var updatedTask = task with { 
                        ResponsiblePerson = person,
                        UpdatedAt = DateTime.UtcNow 
                    };
                    SubmitTodoUpdate(host, updatedTask);
                    return Task.CompletedTask;
                })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => HeadingButtonStyle(style));
            
            assignButton = assignButton.WithView(assignToPerson);
        }

        return assignButton;
    }

    /// <summary>
    /// Auto-assigns tasks to team members using load balancing
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="tasksToAssign">Tasks to assign</param>
    private static void AutoAssignTasks(LayoutAreaHost host, IEnumerable<TodoItem> tasksToAssign)
    {
        var availablePersons = ResponsiblePersons.AvailablePersons.ToList();
        var personIndex = 0;
        var updatedTasks = new List<TodoItem>();

        foreach (var task in tasksToAssign)
        {
            var assignedPerson = availablePersons[personIndex % availablePersons.Count];
            var updatedTask = task with { 
                ResponsiblePerson = assignedPerson,
                UpdatedAt = DateTime.UtcNow 
            };
            updatedTasks.Add(updatedTask);
            personIndex++;
        }

        if (updatedTasks.Any())
        {
            var changeRequest = new DataChangeRequest()
                .WithUpdates(updatedTasks);

            host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));
        }
    }

    /// <summary>
    /// Creates an action button for status groups in the summary view
    /// </summary>
    /// <param name="status">The todo status</param>
    /// <param name="statusGroup">The group of todos with this status</param>
    /// <param name="host">The layout area host</param>
    /// <returns>An action button for the status group</returns>
    private static UiControl CreateStatusSummaryButton(TodoStatus status, IGrouping<TodoStatus, TodoItem> statusGroup, LayoutAreaHost host)
    {
        return status switch
        {
            TodoStatus.Pending => Controls.MenuItem("▶️ Start All", "play")
                .WithClickAction(_ => { UpdateAllTodosInGroup(host, statusGroup, TodoStatus.InProgress); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => style.WithDisplay("flex").WithAlignItems("center").WithJustifyContent("flex-end")),
            
            TodoStatus.InProgress => Controls.MenuItem("✅ Complete All", "check-circle")
                .WithClickAction(_ => { UpdateAllTodosInGroup(host, statusGroup, TodoStatus.Completed); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => style.WithDisplay("flex").WithAlignItems("center").WithJustifyContent("flex-end")),
            
            TodoStatus.Completed => Controls.MenuItem("📦 Archive All", "archive")
                .WithClickAction(_ => { DeleteAllTodosInGroup(host, statusGroup); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => style.WithDisplay("flex").WithAlignItems("center").WithJustifyContent("flex-end")),
            
            TodoStatus.Cancelled => Controls.MenuItem("🔄 Restore All", "refresh-cw")
                .WithClickAction(_ => { UpdateAllTodosInGroup(host, statusGroup, TodoStatus.Pending); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => style.WithDisplay("flex").WithAlignItems("center").WithJustifyContent("flex-end")),
            
            _ => Controls.Html("")
        };
    }

    /// <summary>
    /// Creates an action button for category groups
    /// </summary>
    /// <param name="categoryTodos">The todos in this category</param>
    /// <param name="host">The layout area host</param>
    /// <returns>An action button for the category</returns>
    private static UiControl CreateCategoryActionButton(List<TodoItem> categoryTodos, LayoutAreaHost host)
    {
        var incompleteTodos = categoryTodos.Where(t => t.Status != TodoStatus.Completed).ToList();
        var pendingTodos = categoryTodos.Where(t => t.Status == TodoStatus.Pending).ToList();

        // Determine the primary action based on category state
        if (incompleteTodos.Any())
        {
            if (pendingTodos.Count == incompleteTodos.Count)
            {
                // All incomplete todos are pending - offer to start all
                return Controls.MenuItem("▶️ Start All", "play")
                    .WithClickAction(_ => { UpdateAllTodosInGroup(host, pendingTodos, TodoStatus.InProgress); return Task.CompletedTask; })
                    .WithWidth(MenuWidth)
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(style => HeadingButtonStyle(style));
            }
            else
            {
                // Mixed status - offer to complete all incomplete
                return Controls.MenuItem("✅ Complete All", "check-circle")
                    .WithClickAction(_ => { UpdateAllTodosInGroup(host, incompleteTodos, TodoStatus.Completed); return Task.CompletedTask; })
                    .WithWidth(MenuWidth)
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(style => HeadingButtonStyle(style));
            }
        }
        else
        {
            // All todos are completed - offer to archive category
            return Controls.MenuItem("📦 Archive All", "archive")
                .WithClickAction(_ => { DeleteAllTodosInGroup(host, categoryTodos); return Task.CompletedTask; })
                .WithWidth(MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => HeadingButtonStyle(style));
        }
    }

    /// <summary>
    /// Shows overdue reminder dialog for a todo item
    /// </summary>
    /// <param name="todo">The overdue todo item</param>
    /// <param name="host">The layout area host</param>
    private static void ShowOverdueReminderDialog(TodoItem todo, LayoutAreaHost host)
    {
        var defaultEmailText = $"Hi {todo.ResponsiblePerson},\n\n" +
            $"This is a reminder that the following task is overdue:\n\n" +
            $"Task: {todo.Title}\n" +
            $"Description: {todo.Description}\n" +
            $"Due Date: {todo.DueDate:yyyy-MM-dd}\n\n" +
            $"Please complete this task as soon as possible.\n\n" +
            $"Thanks!";

        // Create the dialog content
        var reminderForm = Controls.Stack
            .WithView(Controls.H5("Send Overdue Reminder")
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(Controls.Markdown($"**Task:** {todo.Title}")
                .WithStyle(style => style.WithMarginBottom("5px")))
            .WithView(Controls.Markdown($"**Assigned to:** {ResponsiblePersons.GetDisplayName(todo.ResponsiblePerson)}")
                .WithStyle(style => style.WithMarginBottom("5px")))
            .WithView(Controls.Markdown($"**Due:** {todo.DueDate:yyyy-MM-dd} ({Math.Abs((DateTime.Now - todo.DueDate!.Value).Days)} days overdue)")
                .WithStyle(style => style.WithMarginBottom("8px").WithColor("var(--color-danger-fg)")))
            .WithView(Controls.Markdown("**Email Text:**")
                .WithStyle(style => style.WithMarginBottom("5px")))
            .WithView(Controls.Html($"<textarea rows='8' style='width: 100%; padding: 8px; border: 1px solid var(--color-border-default); border-radius: 4px; font-family: inherit; resize: vertical;'>{defaultEmailText.Replace("<", "&lt;").Replace(">", "&gt;")}</textarea>")
                .WithStyle(style => style.WithMarginBottom("8px")))
            .WithView(Controls.Stack
                .WithView(Controls.Button("📧 Send Reminder")
                    .WithClickAction(_ =>
                    {
                        // In a real implementation, this would send an email
                        System.Console.WriteLine($"OVERDUE REMINDER SENT:");
                        System.Console.WriteLine($"To: {todo.ResponsiblePerson}");
                        System.Console.WriteLine($"Subject: Overdue Task Reminder - {todo.Title}");
                        System.Console.WriteLine($"Body:\n{defaultEmailText}");
                        System.Console.WriteLine("=".PadRight(50, '='));

                        // Close the dialog
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button("❌ Cancel")
                    .WithClickAction(_ =>
                    {
                        // Close the dialog without action
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return Task.CompletedTask;
                    }))
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(10)
                .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

        // Create the dialog
        var dialog = Controls.Dialog(reminderForm, "Overdue Reminder")
            .WithSize("M")
            .WithClosable(false);

        // Show the dialog
        host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    /// <summary>
    /// Creates a menu button for overdue reminder functionality
    /// </summary>
    /// <param name="todo">The overdue todo item</param>
    /// <param name="host">The layout area host</param>
    /// <returns>Menu item for sending overdue reminders</returns>
    private static UiControl CreateOverdueReminderButton(TodoItem todo, LayoutAreaHost host)
    {
        return Controls.MenuItem("📧 Send Reminder", "send")
            .WithAppearance(Appearance.Neutral)
            .WithClickAction(_ => {
                // Show the overdue reminder dialog
                ShowOverdueReminderDialog(todo, host);
                return Task.CompletedTask;
            })
            .WithWidth(MenuWidth)
            .WithStyle(style => HeadingButtonStyle(style));
    }
}
