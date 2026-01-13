// <meshweaver>
// Id: TodoViews
// DisplayName: Todo Views
// </meshweaver>

/// <summary>
/// Custom views for Todo items.
/// </summary>
public static class TodoViews
{
    /// <summary>
    /// Details view showing the Todo item with status, metadata, and action buttons.
    /// Enhanced with status promotion menu and assignee thumbnail.
    /// </summary>
    public static IObservable<UiControl?> Details(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetStream<Todo>()
            ?.Select(todos => todos?.FirstOrDefault())
            .Select(todo => BuildTodoDetails(host, todo))
            ?? Observable.Return<UiControl?>(Controls.Markdown("*Loading...*"));
    }

    private static UiControl BuildTodoDetails(LayoutAreaHost host, Todo? todo)
    {
        if (todo == null)
            return Controls.Markdown("*Todo not found*");

        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Header row with status icon, title, priority and status badges
        var statusIcon = GetStatusIcon(todo.Status);
        var priorityBadge = GetPriorityBadge(todo.Priority);
        var statusBadge = GetStatusBadge(todo.Status);

        mainGrid = mainGrid.WithView(
            Controls.Html($@"
                <div style=""display: flex; align-items: center; gap: 12px; margin-bottom: 16px; flex-wrap: wrap;"">
                    <span style=""font-size: 28px;"">{statusIcon}</span>
                    <h1 style=""margin: 0; flex: 1; min-width: 200px;"">{System.Web.HttpUtility.HtmlEncode(todo.Title)}</h1>
                    {priorityBadge}
                    {statusBadge}
                </div>"),
            skin => skin.WithXs(12));

        // Description if present
        if (!string.IsNullOrEmpty(todo.Description))
        {
            mainGrid = mainGrid.WithView(
                Controls.Markdown(todo.Description)
                    .WithStyle(style => style.WithMarginBottom("20px").WithColor("var(--neutral-foreground-hint)")),
                skin => skin.WithXs(12));
        }

        // Two-column layout: Details card (left) and Assignee card (right)
        // Details card
        mainGrid = mainGrid.WithView(
            BuildDetailsCard(todo),
            skin => skin.WithXs(12).WithMd(8));

        // Assignee card
        mainGrid = mainGrid.WithView(
            BuildAssigneeCard(host, todo.Assignee),
            skin => skin.WithXs(12).WithMd(4));

        // Status promotion menu - all statuses with most likely first
        mainGrid = mainGrid.WithView(
            BuildStatusPromotionMenu(host, todo),
            skin => skin.WithXs(12));

        return mainGrid;
    }

    private static UiControl BuildDetailsCard(Todo todo)
    {
        var cardContent = new System.Text.StringBuilder();
        cardContent.AppendLine($"**Category:** {todo.Category}\n");
        cardContent.AppendLine($"**Priority:** {GetPriorityLabel(todo.Priority)}\n");
        if (todo.DueDate.HasValue)
            cardContent.AppendLine($"**Due Date:** {todo.DueDate.Value:MMMM dd, yyyy} {GetDueDateIndicator(todo.DueDate.Value, todo.Status)}\n");
        cardContent.AppendLine($"**Created:** {todo.CreatedAt:MMMM dd, yyyy}\n");
        if (todo.CompletedAt.HasValue)
            cardContent.AppendLine($"**Completed:** {todo.CompletedAt.Value:MMMM dd, yyyy}\n");

        return Controls.Html($@"
            <div style=""padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; background: var(--neutral-layer-2); margin-bottom: 16px;"">
                <h3 style=""margin: 0 0 12px 0; font-size: 14px; color: var(--neutral-foreground-hint); text-transform: uppercase; letter-spacing: 0.5px;"">Details</h3>
                <div style=""line-height: 1.8;"">
                    <div><strong>Category:</strong> {todo.Category}</div>
                    <div><strong>Priority:</strong> {GetPriorityLabel(todo.Priority)}</div>
                    {(todo.DueDate.HasValue ? $"<div><strong>Due Date:</strong> {todo.DueDate.Value:MMMM dd, yyyy} {GetDueDateIndicator(todo.DueDate.Value, todo.Status)}</div>" : "")}
                    <div><strong>Created:</strong> {todo.CreatedAt:MMMM dd, yyyy}</div>
                    {(todo.CompletedAt.HasValue ? $"<div><strong>Completed:</strong> {todo.CompletedAt.Value:MMMM dd, yyyy}</div>" : "")}
                </div>
            </div>");
    }

    private static UiControl BuildAssigneeCard(LayoutAreaHost host, string? assignee)
    {
        if (string.IsNullOrEmpty(assignee))
        {
            return Controls.Html($@"
                <div style=""padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; background: var(--neutral-layer-2); margin-bottom: 16px; text-align: center;"">
                    <h3 style=""margin: 0 0 12px 0; font-size: 14px; color: var(--neutral-foreground-hint); text-transform: uppercase; letter-spacing: 0.5px;"">Assignee</h3>
                    <div style=""width: 64px; height: 64px; border-radius: 50%; background: var(--neutral-layer-3); margin: 0 auto 12px; display: flex; align-items: center; justify-content: center; font-size: 24px;"">\ud83d\udc64</div>
                    <div style=""color: var(--neutral-foreground-hint);"">Unassigned</div>
                </div>");
        }

        // Try to get user info from User namespace
        // For now, display basic info with link to user profile
        var avatarUrl = $"/static/storage/content/{assignee}/avatar.svg";
        return Controls.Html($@"
            <div style=""padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; background: var(--neutral-layer-2); margin-bottom: 16px; text-align: center;"">
                <h3 style=""margin: 0 0 12px 0; font-size: 14px; color: var(--neutral-foreground-hint); text-transform: uppercase; letter-spacing: 0.5px;"">Assignee</h3>
                <a href=""/User/{assignee}"" style=""text-decoration: none; color: inherit;"">
                    <img src=""{avatarUrl}"" alt=""{assignee}"" style=""width: 64px; height: 64px; border-radius: 50%; margin-bottom: 12px; background: var(--neutral-layer-3);"" onerror=""this.style.display='none'; this.nextElementSibling.style.display='flex';""/>
                    <div style=""width: 64px; height: 64px; border-radius: 50%; background: var(--neutral-layer-3); margin: 0 auto 12px; display: none; align-items: center; justify-content: center; font-size: 24px;"">\ud83d\udc64</div>
                    <div style=""font-weight: 600;"">{assignee}</div>
                    <div style=""font-size: 12px; color: var(--accent-foreground-rest);"">View Profile \u2192</div>
                </a>
            </div>");
    }

    private static UiControl BuildStatusPromotionMenu(LayoutAreaHost host, Todo todo)
    {
        var buttonStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(style => style.WithGap("8px").WithMarginTop("16px").WithFlexWrap("wrap"));

        // Get ordered statuses with most likely first
        var orderedStatuses = GetOrderedStatusTransitions(todo.Status);

        var isFirst = true;
        foreach (var (label, status, icon) in orderedStatuses)
        {
            if (status == todo.Status) continue; // Skip current status

            buttonStack = buttonStack.WithView(
                Controls.Button($"{icon} {label}")
                    .WithAppearance(isFirst ? Appearance.Accent : Appearance.Neutral)
                    .WithClickAction(_ =>
                    {
                        UpdateTodoStatus(host, todo, status);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }));
            isFirst = false;
        }

        return Controls.Stack
            .WithView(Controls.Html("<h4 style=\"margin: 16px 0 8px 0; font-size: 12px; color: var(--neutral-foreground-hint); text-transform: uppercase; letter-spacing: 0.5px;\">Change Status</h4>"))
            .WithView(buttonStack);
    }

    private static IEnumerable<(string Label, TodoStatus Status, string Icon)> GetOrderedStatusTransitions(TodoStatus currentStatus)
    {
        // Return all statuses ordered by likelihood based on current status
        // Most likely transition first, then others
        switch (currentStatus)
        {
            case TodoStatus.Pending:
                yield return ("Start", TodoStatus.InProgress, "\ud83d\udd04");
                yield return ("Complete", TodoStatus.Completed, "\u2705");
                yield return ("Block", TodoStatus.Blocked, "\ud83d\udeab");
                yield return ("Review", TodoStatus.InReview, "\ud83d\udc41\ufe0f");
                break;
            case TodoStatus.InProgress:
                yield return ("Complete", TodoStatus.Completed, "\u2705");
                yield return ("Send for Review", TodoStatus.InReview, "\ud83d\udc41\ufe0f");
                yield return ("Pause", TodoStatus.Pending, "\u23f3");
                yield return ("Block", TodoStatus.Blocked, "\ud83d\udeab");
                break;
            case TodoStatus.InReview:
                yield return ("Approve", TodoStatus.Completed, "\u2705");
                yield return ("Return to Progress", TodoStatus.InProgress, "\ud83d\udd04");
                yield return ("Block", TodoStatus.Blocked, "\ud83d\udeab");
                yield return ("Back to Pending", TodoStatus.Pending, "\u23f3");
                break;
            case TodoStatus.Blocked:
                yield return ("Unblock", TodoStatus.InProgress, "\ud83d\udd04");
                yield return ("Return to Pending", TodoStatus.Pending, "\u23f3");
                yield return ("Complete Anyway", TodoStatus.Completed, "\u2705");
                yield return ("Review", TodoStatus.InReview, "\ud83d\udc41\ufe0f");
                break;
            case TodoStatus.Completed:
                yield return ("Reopen", TodoStatus.InProgress, "\ud83d\udd04");
                yield return ("Back to Pending", TodoStatus.Pending, "\u23f3");
                yield return ("Review Again", TodoStatus.InReview, "\ud83d\udc41\ufe0f");
                yield return ("Mark Blocked", TodoStatus.Blocked, "\ud83d\udeab");
                break;
        }
    }

    private static string GetStatusBadge(TodoStatus status)
    {
        var (bg, text) = status switch
        {
            TodoStatus.Pending => ("#ffc107", "#000"),
            TodoStatus.InProgress => ("#0d6efd", "#fff"),
            TodoStatus.InReview => ("#6f42c1", "#fff"),
            TodoStatus.Completed => ("#198754", "#fff"),
            TodoStatus.Blocked => ("#dc3545", "#fff"),
            _ => ("#6c757d", "#fff")
        };
        return $"<span style=\"background: {bg}; color: {text}; padding: 4px 12px; border-radius: 20px; font-size: 12px; font-weight: 600;\">{status}</span>";
    }

    private static void UpdateTodoStatus(LayoutAreaHost host, Todo todo, TodoStatus newStatus)
    {
        var updatedTodo = todo with
        {
            Status = newStatus,
            CompletedAt = newStatus == TodoStatus.Completed ? DateTimeOffset.UtcNow : null
        };

        var changeRequest = new DataChangeRequest().WithUpdates(updatedTodo);
        host.Hub.Post(changeRequest, o => o.WithTarget(host.Hub.Address));
    }

    // Team members for assignment dropdown
    private static readonly string[] TeamMembers = { "Alice", "Bob", "Carol", "David", "Emma", "Roland" };

    /// <summary>
    /// Thumbnail view for catalog listings - enhanced with status menu, assignment dropdown, and reminder button.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.Workspace.GetStream<Todo>()
            ?.Select(todos => todos?.FirstOrDefault())
            .Select(todo => BuildThumbnail(host, todo, hubPath))
            ?? Observable.Return<UiControl?>(null);
    }

    private static UiControl BuildThumbnail(LayoutAreaHost host, Todo? todo, string hubPath)
    {
        if (todo == null)
            return Controls.Html("");

        var statusIcon = GetStatusIcon(todo.Status);
        var priorityBadge = GetPriorityBadge(todo.Priority);
        var statusColor = GetStatusColor(todo.Status);
        var isOverdue = IsOverdue(todo);

        // Build the card with interactive elements
        var stack = Controls.Stack.WithStyle(style => style
            .WithPadding("12px")
            .WithBorder("1px solid var(--neutral-stroke-rest)")
            .WithBorderRadius("8px")
            .WithBackgroundColor("var(--neutral-layer-2)")
            .WithBorderLeft($"3px solid {statusColor}"));

        // Header row: Status icon, title, priority badge, and link
        stack = stack.WithView(Controls.Html($@"
            <div style=""display: flex; align-items: center; gap: 8px; margin-bottom: 8px;"">
                <span style=""font-size: 16px;"">{statusIcon}</span>
                <a href=""/{hubPath}"" style=""text-decoration: none; color: inherit; font-weight: 600; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"">
                    {System.Web.HttpUtility.HtmlEncode(todo.Title)}
                </a>
                {priorityBadge}
            </div>"));

        // Category and due date info
        var dueDateHtml = todo.DueDate.HasValue
            ? $"<span style=\"font-size: 11px;\">Due: {todo.DueDate.Value:MMM dd} {GetDueDateIndicator(todo.DueDate.Value, todo.Status)}</span>"
            : "";
        stack = stack.WithView(Controls.Html($@"
            <div style=""display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px; font-size: 12px; color: var(--neutral-foreground-hint);"">
                <span>{todo.Category}</span>
                {dueDateHtml}
            </div>"));

        // Action row: Status dropdown, Assignee dropdown, Reminder button
        var actionRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(style => style.WithGap("8px").WithFlexWrap("wrap").WithAlignItems("center"));

        // Compact status menu - show next likely action as primary button
        if (todo.Status != TodoStatus.Completed)
        {
            var (nextLabel, nextStatus, nextIcon) = GetPrimaryTransition(todo.Status);
            actionRow = actionRow.WithView(
                Controls.Button($"{nextIcon}")
                    .WithLabel($"{nextLabel}")
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px"))
                    .WithClickAction(_ =>
                    {
                        UpdateTodoStatus(host, todo, nextStatus);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }));
        }

        // Assignment indicator with quick-assign action
        var assigneeDisplay = string.IsNullOrEmpty(todo.Assignee) ? "\ud83d\udc64" : todo.Assignee.Substring(0, 1);
        var assigneeTitle = string.IsNullOrEmpty(todo.Assignee) ? "Unassigned - Click to assign" : $"Assigned to {todo.Assignee} - Click to reassign";
        actionRow = actionRow.WithView(
            Controls.Button(assigneeDisplay)
                .WithLabel(assigneeTitle)
                .WithAppearance(string.IsNullOrEmpty(todo.Assignee) ? Appearance.Neutral : Appearance.Accent)
                .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px").WithBorderRadius("50%"))
                .WithClickAction(_ =>
                {
                    // Cycle through team members or unassign
                    var nextAssignee = GetNextAssignee(todo.Assignee);
                    UpdateTodoAssignee(host, todo, nextAssignee);
                    return System.Threading.Tasks.Task.CompletedTask;
                }));

        // Reminder button for overdue items only
        if (isOverdue)
        {
            actionRow = actionRow.WithView(
                Controls.Button("\u23f0")
                    .WithLabel("Send reminder")
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px").WithColor("#dc3545"))
                    .WithClickAction(_ =>
                    {
                        SendReminder(host, todo);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }));
        }

        stack = stack.WithView(actionRow);

        return stack;
    }

    private static (string Label, TodoStatus Status, string Icon) GetPrimaryTransition(TodoStatus currentStatus) => currentStatus switch
    {
        TodoStatus.Pending => ("Start", TodoStatus.InProgress, "\ud83d\udd04"),
        TodoStatus.InProgress => ("Complete", TodoStatus.Completed, "\u2705"),
        TodoStatus.InReview => ("Approve", TodoStatus.Completed, "\u2705"),
        TodoStatus.Blocked => ("Unblock", TodoStatus.InProgress, "\ud83d\udd04"),
        _ => ("Reopen", TodoStatus.InProgress, "\ud83d\udd04")
    };

    private static string? GetNextAssignee(string? currentAssignee)
    {
        if (string.IsNullOrEmpty(currentAssignee))
            return TeamMembers[0];

        var currentIndex = Array.IndexOf(TeamMembers, currentAssignee);
        if (currentIndex < 0 || currentIndex >= TeamMembers.Length - 1)
            return null; // Unassign after cycling through all
        return TeamMembers[currentIndex + 1];
    }

    private static void UpdateTodoAssignee(LayoutAreaHost host, Todo todo, string? newAssignee)
    {
        var updatedTodo = todo with { Assignee = newAssignee };
        var changeRequest = new DataChangeRequest().WithUpdates(updatedTodo);
        host.Hub.Post(changeRequest, o => o.WithTarget(host.Hub.Address));
    }

    private static bool IsOverdue(Todo todo)
    {
        if (todo.Status == TodoStatus.Completed || !todo.DueDate.HasValue)
            return false;
        return todo.DueDate.Value.Date < DateTimeOffset.Now.Date;
    }

    private static void SendReminder(LayoutAreaHost host, Todo todo)
    {
        // Demo mode: Log the reminder action
        // In production, this would send an email or notification
        var assignee = todo.Assignee ?? "team";
        var message = $"Reminder sent to {assignee} for overdue task: {todo.Title}";

        // Log to console for demo purposes
        System.Console.WriteLine($"[REMINDER] {message}");

        // Show a toast/notification by posting a notification message
        // For now, just log - the UI framework would handle toast display
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

    private static string GetStatusColor(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "#ffc107",
        TodoStatus.InProgress => "#0d6efd",
        TodoStatus.InReview => "#6f42c1",
        TodoStatus.Completed => "#198754",
        TodoStatus.Blocked => "#dc3545",
        _ => "#6c757d"
    };

    private static string GetPriorityBadge(TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => "<span style=\"background: #dc3545; color: white; padding: 2px 6px; border-radius: 4px; font-size: 10px; font-weight: 600;\">CRITICAL</span>",
        TaskPriority.High => "<span style=\"background: #fd7e14; color: white; padding: 2px 6px; border-radius: 4px; font-size: 10px; font-weight: 600;\">HIGH</span>",
        TaskPriority.Medium => "",
        TaskPriority.Low => "<span style=\"background: #6c757d; color: white; padding: 2px 6px; border-radius: 4px; font-size: 10px;\">LOW</span>",
        _ => ""
    };

    private static string GetPriorityLabel(TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => "\ud83d\udea8 Critical",
        TaskPriority.High => "\ud83d\udd25 High",
        TaskPriority.Medium => "\u2796 Medium",
        TaskPriority.Low => "\u2b07\ufe0f Low",
        _ => priority.ToString()
    };

    private static string GetDueDateIndicator(DateTimeOffset dueDate, TodoStatus status)
    {
        if (status == TodoStatus.Completed) return "";

        var today = DateTimeOffset.Now.Date;
        var dueDateOnly = dueDate.Date;

        if (dueDateOnly < today)
            return "<span style=\"color: #dc3545; font-weight: 600;\">OVERDUE</span>";
        if (dueDateOnly == today)
            return "<span style=\"color: #fd7e14; font-weight: 600;\">TODAY</span>";
        if (dueDateOnly == today.AddDays(1))
            return "<span style=\"color: #ffc107;\">Tomorrow</span>";

        return "";
    }
}
