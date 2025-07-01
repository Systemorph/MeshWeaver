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
public static class TodoLayoutArea
{
    /// <summary>
    /// Creates a TodoList layout area that subscribes to a stream of todo items
    /// and displays them in an interactive format with action buttons
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control that updates when todo items change</returns>
    public static IObservable<UiControl> TodoList(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        // Subscribe to the stream of TodoItem entities from the data source
        return host.Workspace
            .GetStream<TodoItem>()
            .Select(todoItems => CreateInteractiveTodoListStack(todoItems, host))
            .StartWith(Controls.Markdown("# Todo List\n\n*Loading todo items...*"));
    }

    /// <summary>
    /// Creates a TodosByCategory layout area that groups todos by category
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control showing todos grouped by category</returns>
    public static IObservable<UiControl> TodosByCategory(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        return host.Workspace
            .GetStream<TodoItem>()
            .Select(CreateTodosByCategoryMarkdown)
            .StartWith(Controls.Markdown("# Todos by Category\n\n*Loading todo items...*"));
    }

    /// <summary>
    /// Creates a TodoSummary layout area that shows summary statistics
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An observable UI control showing todo summary statistics</returns>
    public static IObservable<UiControl> TodoSummary(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        return host.Workspace
            .GetStream<TodoItem>()
            .Select(CreateTodoSummaryMarkdown)
            .StartWith(Controls.Markdown("# Todo Summary\n\n*Loading todo statistics...*"));
    }

    /// <summary>
    /// Creates summary statistics for todo items in markdown format
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <returns>A markdown control with summary statistics</returns>
    private static UiControl CreateTodoSummaryMarkdown(IReadOnlyCollection<TodoItem> todoItems)
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
        var categoryGroups = todoItems.GroupBy(t => t.Category ?? "Uncategorized");
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

        return Controls.Markdown(sb.ToString());
    }

    /// <summary>
    /// Creates todos grouped by category in markdown format
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <returns>A markdown control with todos grouped by category</returns>
    private static UiControl CreateTodosByCategoryMarkdown(IReadOnlyCollection<TodoItem> todoItems)
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
            .GroupBy(t => t.Category ?? "Uncategorized")
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

        if (!string.IsNullOrEmpty(todo.Category))
        {
            sb.AppendLine($"**Category:** {todo.Category}");
        }

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
    /// Creates an interactive todo list with a clean layout grid structure and vertically aligned action buttons
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>A LayoutGrid control with structured todo items</returns>
    private static UiControl CreateInteractiveTodoListStack(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost host)
    {
        // Create main LayoutGrid with minimal spacing
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // First row: Title and Add New Todo button
        mainGrid = mainGrid
            .WithView(Controls.H2("📝 Todo List with Actions")
                .WithStyle(style => style.WithMarginBottom("10px").WithColor("var(--color-fg-default)")),
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

        // Group by status for better organization
        var statusGroups = orderedTodos.GroupBy(t => t.Status).ToList();

        foreach (var statusGroup in statusGroups.OrderBy(g => (int)g.Key))
        {
            var statusIcon = GetStatusIcon(statusGroup.Key);
            var statusName = statusGroup.Key.ToString();

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

            // Status group header row with aligned content
            mainGrid = mainGrid
                .WithView(Controls.H3($"{statusIcon} {statusName} ({statusGroup.Count()})")
                    .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("10px").WithColor("var(--color-fg-default)").WithDisplay("flex").WithAlignItems("center")),
                    skin => skin.WithXs(12).WithSm(9).WithMd(10))
                .WithView(Controls.Stack
                    .WithView(statusActionButton)
                    .WithStyle(style => HeadingButtonStyle(style)),
                    skin => skin.WithXs(12).WithSm(3).WithMd(2));

            // Todo items in this status group
            foreach (var todo in statusGroup)
            {
                var (todoContent, todoActions) = CreateTodoItemContentAndActions(todo, host);

                mainGrid = mainGrid
                    .WithView(todoContent,
                        skin => skin.WithXs(12).WithSm(9).WithMd(10))
                    .WithView(todoActions,
                        skin => skin.WithXs(12).WithSm(3).WithMd(2));
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
            DueDate = DateTime.Now.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Define the area ID for the new todo data
        const string newTodoDataId = "NewTodoData";

        // Create an edit form for the new todo item with proper data binding
        var editForm = Controls.Stack
            .WithView(Controls.H3("Create New Todo")
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(host.Edit(newTodo, newTodoDataId)
                .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), newTodoDataId)
            .WithView(Controls.Stack
                .WithView(Controls.Button("💾 Save Todo")
                    .WithClickAction(_ =>
                    {
                        // Changes are saved immediately ==> just
                        // Close the dialog by clearing the dialog area
                        host.UpdateArea(DialogControl.DialogArea, null);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button("❌ Cancel")
                    .WithClickAction(_ =>
                    {
                        // since we have saved immediately, we need to now delete the entity.
                        host.Hub.Post(new DataChangeRequest() { Deletions = [newTodo] }, o => o.WithTarget(TodoApplicationAttribute.Address));

                        // Close the dialog by clearing the dialog area
                        host.UpdateArea(DialogControl.DialogArea, null);
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
            .WithDeletions(todos);

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
        if (!string.IsNullOrEmpty(todo.Category) && todo.Category != "General")
        {
            sb.Append($" `{todo.Category}`");
        }

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
            .WithView(Controls.H3("Edit Todo")
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(host.Edit(todoToEdit, editTodoDataId)
                .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), editTodoDataId)
            .WithView(Controls.Stack
                .WithView(Controls.Button("💾 Done")
                    .WithClickAction(_ =>
                    {
                        // is updated on the fly, so we just need to close the dialog
                        // Close the dialog by clearing the dialog area
                        host.UpdateArea(DialogControl.DialogArea, null);
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
                        host.UpdateArea(DialogControl.DialogArea, null);
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
}
