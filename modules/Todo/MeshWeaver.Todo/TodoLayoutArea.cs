using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Todo;

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
        var overdue = todoItems.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date < now && t.Status != TodoStatus.Completed).Count();
        var dueToday = todoItems.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date == now && t.Status != TodoStatus.Completed).Count();
        var dueSoon = todoItems.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date > now && t.DueDate.Value.Date <= now.AddDays(7) && t.Status != TodoStatus.Completed).Count();

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
        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(0));

        // First row: Title and Add New Todo button
        mainGrid = mainGrid
            .WithView(Controls.H2("📝 Todo List with Actions")
                .WithStyle(style => style.WithMarginBottom("10px").WithColor("#2c3e50")),
                skin => skin.WithXs(12).WithSm(8).WithMd(9))
            .WithView(Controls.Button("➕ Add New Todo")
                .WithClickAction(_ => { SubmitNewTodo(host); return Task.CompletedTask; })
                .WithStyle(style => style.WithMarginBottom("20px")),
                skin => skin.WithXs(12).WithSm(4).WithMd(3));

        if (!todoItems.Any())
        {
            mainGrid = mainGrid
                .WithView(Controls.Markdown("*No todo items found. Click 'Add New Todo' to get started!*")
                    .WithStyle(style => style.WithColor("#666")),
                    skin => skin.WithXs(12).WithSm(8).WithMd(9))
                .WithView(Controls.Html(""),
                    skin => skin.WithXs(12).WithSm(4).WithMd(3));
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

            // Create action button for status header
            UiControl statusActionButton = statusGroup.Key switch
            {
                TodoStatus.Pending => Controls.Button("▶️ Start All")
                    .WithClickAction(_ => { UpdateAllTodosInGroup(host, statusGroup, TodoStatus.InProgress); return Task.CompletedTask; })
                    .WithStyle(style => style.WithBackgroundColor("#28a745").WithColor("white").WithBorder("none").WithPadding("6px 12px").WithBorderRadius("4px")),
                TodoStatus.InProgress => Controls.Button("⏸️ Close All")
                    .WithClickAction(_ => { UpdateAllTodosInGroup(host, statusGroup, TodoStatus.Completed); return Task.CompletedTask; })
                    .WithStyle(style => style.WithBackgroundColor("#ffc107").WithColor("white").WithBorder("none").WithPadding("6px 12px").WithBorderRadius("4px")),
                TodoStatus.Completed => Controls.Button("📦 Archive All")
                    .WithClickAction(_ => { DeleteAllTodosInGroup(host, statusGroup); return Task.CompletedTask; })
                    .WithStyle(style => style.WithBackgroundColor("#6c757d").WithColor("white").WithBorder("none").WithPadding("6px 12px").WithBorderRadius("4px")),
                TodoStatus.Cancelled => Controls.Button("🗑️ Delete All")
                    .WithClickAction(_ => { DeleteAllTodosInGroup(host, statusGroup); return Task.CompletedTask; })
                    .WithStyle(style => style.WithBackgroundColor("#dc3545").WithColor("white").WithBorder("none").WithPadding("6px 12px").WithBorderRadius("4px")),
                _ => Controls.Html("") // Empty placeholder for other statuses
            };

            // Status group header row with aligned content
            mainGrid = mainGrid
                .WithView(Controls.H3($"{statusIcon} {statusName} ({statusGroup.Count()})")
                    .WithStyle(style => style.WithMarginTop("20px").WithMarginBottom("10px").WithColor("#333").WithDisplay("flex").WithAlignItems("center")),
                    skin => skin.WithXs(12).WithSm(8).WithMd(9))
                .WithView(Controls.Stack
                    .WithView(statusActionButton)
                    .WithStyle(style => style.WithDisplay("flex").WithAlignItems("center").WithJustifyContent("flex-end").WithHeight("100%").WithPaddingTop("20px")),
                    skin => skin.WithXs(12).WithSm(4).WithMd(3));

            // Todo items in this status group
            foreach (var todo in statusGroup)
            {
                var (todoContent, todoActions) = CreateTodoItemContentAndActions(todo, host);

                mainGrid = mainGrid
                    .WithView(todoContent,
                        skin => skin.WithXs(12).WithSm(8).WithMd(9))
                    .WithView(todoActions,
                        skin => skin.WithXs(12).WithSm(4).WithMd(3));
            }
        }

        return mainGrid;
    }

    /// <summary>
    /// Creates a structured todo item row with content and collapsible actions
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>A styled todo item row</returns>
    private static UiControl CreateTodoItemRow(TodoItem todo, LayoutAreaHost host)
    {
        var statusIcon = GetStatusIcon(todo.Status);
        var contentMarkdown = CreateCompactTodoContent(todo);
        var actionControls = CreateActionControls(todo, host);

        // Create a horizontal layout using flexbox styling
        return Controls.Stack
            .WithView(
                Controls.Stack
                    .WithView(
                        Controls.Stack
                            .WithView(Controls.Markdown($"{statusIcon}"))
                            .WithStyle(style => style
                                .WithWidth("40px")
                                .WithTextAlign("center")
                                .WithFlexShrink("0")))
                    .WithView(
                        Controls.Stack
                            .WithView(Controls.Markdown(contentMarkdown))
                            .WithStyle(style => style
                                .WithFlexGrow("1")
                                .WithMinWidth("0")
                                .WithPaddingLeft("15px")
                                .WithPaddingRight("15px")))
                    .WithView(
                        Controls.Stack
                            .WithView(actionControls)
                            .WithStyle(style => style
                                .WithWidth("150px")
                                .WithFlexShrink("0")
                                .WithTextAlign("right")))
                    .WithStyle(style => style
                        .WithDisplay("flex")
                        .WithFlexDirection("row")
                        .WithAlignItems("flex-start")
                        .WithGap("0")))
            .WithStyle(style => style
                .WithPadding("15px")
                .WithMarginBottom("10px")
                .WithBorder("1px solid #e1e8ed")
                .WithBorderRadius("8px")
                .WithBackgroundColor("#fafbfc")
                .WithBoxShadow("0 1px 3px rgba(0,0,0,0.1)"));
    }

    /// <summary>
    /// Creates action controls with primary action and menu button for secondary actions
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>A control with action buttons</returns>
    private static UiControl CreateActionControls(TodoItem todo, LayoutAreaHost host)
    {
        var primaryAction = GetPrimaryActionButton(todo, host);
        var hasSecondaryActions = HasSecondaryActions(todo);

        if (!hasSecondaryActions)
        {
            return primaryAction;
        }

        // Create horizontal layout with primary action and menu button
        return Controls.Stack
            .WithView(primaryAction)
            .WithView(CreateMenuButton(todo, host))
            .WithStyle(style => style
                .WithDisplay("flex")
                .WithFlexDirection("row")
                .WithGap("8px")
                .WithAlignItems("center"));
    }

    /// <summary>
    /// Creates a menu button that shows secondary actions (simplified for now)
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <param name="host">The layout area host</param>
    /// <returns>A menu button</returns>
    private static UiControl CreateMenuButton(TodoItem todo, LayoutAreaHost host)
    {
        // For now, create a simple button that shows the most common secondary action
        // In a real implementation, this would be a proper dropdown menu
        var secondaryAction = GetFirstSecondaryAction(todo, host);
        return secondaryAction ?? Controls.Button("⋯")
            .WithClickAction(_ => Task.CompletedTask)
            .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px"));
    }

    /// <summary>
    /// Gets the primary (most likely) action button for a todo item
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <param name="host">The layout area host</param>
    /// <returns>Primary action button</returns>
    private static UiControl GetPrimaryActionButton(TodoItem todo, LayoutAreaHost host)
    {
        return todo.Status switch
        {
            TodoStatus.Pending => Controls.Button("▶️ Start")
                .WithClickAction(_ => { UpdateTodoStatus(host, todo, TodoStatus.InProgress); return Task.CompletedTask; })
                .WithStyle(style => style.WithBackgroundColor("#28a745").WithColor("white").WithBorder("none").WithPadding("6px 12px").WithBorderRadius("4px")),

            TodoStatus.InProgress => Controls.Button("✅ Done")
                .WithClickAction(_ => { UpdateTodoStatus(host, todo, TodoStatus.Completed); return Task.CompletedTask; })
                .WithStyle(style => style.WithBackgroundColor("#17a2b8").WithColor("white").WithBorder("none").WithPadding("6px 12px").WithBorderRadius("4px")),

            TodoStatus.Completed => Controls.Button("🔄 Reopen")
                .WithClickAction(_ => { UpdateTodoStatus(host, todo, TodoStatus.Pending); return Task.CompletedTask; })
                .WithStyle(style => style.WithBackgroundColor("#6c757d").WithColor("white").WithBorder("none").WithPadding("6px 12px").WithBorderRadius("4px")),

            TodoStatus.Cancelled => Controls.Button("🔄 Restore")
                .WithClickAction(_ => { UpdateTodoStatus(host, todo, TodoStatus.Pending); return Task.CompletedTask; })
                .WithStyle(style => style.WithBackgroundColor("#6c757d").WithColor("white").WithBorder("none").WithPadding("6px 12px").WithBorderRadius("4px")),

            _ => Controls.Button("❓")
                .WithClickAction(_ => Task.CompletedTask)
                .WithStyle(style => style.WithPadding("6px 12px"))
        };
    }

    /// <summary>
    /// Checks if the todo item has secondary actions available
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <returns>True if secondary actions are available</returns>
    private static bool HasSecondaryActions(TodoItem todo)
    {
        return todo.Status switch
        {
            TodoStatus.Pending => true,     // Complete, Delete
            TodoStatus.InProgress => true,  // Pause, Delete
            TodoStatus.Completed => true,   // Delete
            TodoStatus.Cancelled => true,   // Delete
            _ => false
        };
    }

    /// <summary>
    /// Gets the first secondary action for simple menu display
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <param name="host">The layout area host</param>
    /// <returns>First secondary action button or null</returns>
    private static UiControl GetFirstSecondaryAction(TodoItem todo, LayoutAreaHost host)
    {
        return todo.Status switch
        {
            TodoStatus.Pending => Controls.Button("✅")
                .WithClickAction(_ => { UpdateTodoStatus(host, todo, TodoStatus.Completed); return Task.CompletedTask; })
                .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px").WithBackgroundColor("#28a745").WithColor("white").WithBorder("none").WithBorderRadius("4px")),

            TodoStatus.InProgress => Controls.Button("⏸️")
                .WithClickAction(_ => { UpdateTodoStatus(host, todo, TodoStatus.Pending); return Task.CompletedTask; })
                .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px").WithBackgroundColor("#ffc107").WithColor("white").WithBorder("none").WithBorderRadius("4px")),

            _ => Controls.Button("🗑️")
                .WithClickAction(_ => { SubmitTodoDelete(host, todo); return Task.CompletedTask; })
                .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px").WithBackgroundColor("#dc3545").WithColor("white").WithBorder("none").WithBorderRadius("4px"))
        };
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
            Id = Guid.NewGuid(),
            Title = "",
            Description = "",
            Status = TodoStatus.Pending,
            Category = "General",
            DueDate = DateTime.Now.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Create an edit form for the new todo item
        var editForm = host.Hub.ServiceProvider.Edit(newTodo, (todo, editHost, ctx) =>
        {
            return Controls.Stack
                .WithView(Controls.H3("Create New Todo"))
                .WithView(Controls.Stack
                    .WithView(Controls.Button("💾 Save Todo")
                        .WithClickAction(_ =>
                        {
                            // Validate required fields
                            if (string.IsNullOrWhiteSpace(todo?.Title))
                            {
                                return Task.CompletedTask; // Could add validation message here
                            }

                            // Submit the new todo
                            var changeRequest = new DataChangeRequest()
                                .WithCreations(todo with
                                {
                                    Id = Guid.NewGuid(), // Ensure new ID
                                    CreatedAt = DateTime.UtcNow,
                                    UpdatedAt = DateTime.UtcNow
                                });

                            host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));

                            // Close the dialog by clearing the dialog area
                            host.UpdateArea(DialogControl.DialogArea, Controls.Html(""));
                            return Task.CompletedTask;
                        }))
                    .WithView(Controls.Button("❌ Cancel")
                        .WithClickAction(_ =>
                        {
                            // Close the dialog by clearing the dialog area
                            host.UpdateArea(DialogControl.DialogArea, Controls.Html(""));
                            return Task.CompletedTask;
                        }))
                    .WithOrientation(Orientation.Horizontal)
                    .WithHorizontalGap(10))
                .WithVerticalGap(15);
        });

        // Create a dialog with the edit form content - DialogControl.Render() will handle the UiControl rendering
        var dialog = Controls.Dialog(editForm, "Create New Todo")
            .WithSize("M")
            .WithClosable(true)
            .WithCloseAction(_ =>
            {
                // Clear the dialog area when closed
                host.UpdateArea(DialogControl.DialogArea, Controls.Html(""));
            });

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

        // Create content control with icon and text
        var content = Controls.Stack
            .WithView(
                Controls.Stack
                    .WithView(Controls.Markdown($"{statusIcon}"))
                    .WithStyle(style => style
                        .WithWidth("40px")
                        .WithTextAlign("center")
                        .WithFlexShrink("0")))
            .WithView(
                Controls.Stack
                    .WithView(Controls.Markdown(contentMarkdown))
                    .WithStyle(style => style
                        .WithFlexGrow("1")
                        .WithMinWidth("0")
                        .WithPaddingLeft("15px")))
            .WithStyle(style => style
                .WithDisplay("flex")
                .WithFlexDirection("row")
                .WithAlignItems("flex-start")
                .WithPadding("15px")
                .WithMarginBottom("10px")
                .WithBorder("1px solid #e1e8ed")
                .WithBorderRadius("8px")
                .WithBackgroundColor("#fafbfc")
                .WithBoxShadow("0 1px 3px rgba(0,0,0,0.1)"));

        // Create actions control
        var actions = Controls.Stack
            .WithView(actionControls)
            .WithStyle(style => style
                .WithTextAlign("right")
                .WithPadding("15px")
                .WithMarginBottom("10px"));

        return (content, actions);
    }
}
