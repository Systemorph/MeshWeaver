// <meshweaver>
// Id: TodoViews
// DisplayName: Todo Views
// </meshweaver>

using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

/// <summary>
/// Custom views for Todo items.
/// </summary>
public static class TodoViews
{
    /// <summary>
    /// Extracts Todo from MeshNode content, handling various serialization formats
    /// and cross-assembly type mismatches.
    /// </summary>
    private static Todo? ExtractTodo(MeshNode? node)
    {
        if (node?.Content == null)
            return null;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        // Direct type match
        if (node.Content is Todo todo)
            return todo;

        // JsonElement - deserialize to Todo
        if (node.Content is System.Text.Json.JsonElement json)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<Todo>(json.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }

        // Dictionary - convert to JSON then deserialize
        if (node.Content is System.Collections.IDictionary dict)
        {
            try
            {
                var jsonStr = System.Text.Json.JsonSerializer.Serialize(dict);
                return System.Text.Json.JsonSerializer.Deserialize<Todo>(jsonStr, options);
            }
            catch
            {
                return null;
            }
        }

        // Fallback: Content might be a Todo from a different assembly
        // Serialize to JSON and deserialize to local Todo type
        var contentTypeName = node.Content.GetType().Name;
        if (contentTypeName == "Todo" || contentTypeName.EndsWith(".Todo"))
        {
            try
            {
                var jsonStr = System.Text.Json.JsonSerializer.Serialize(node.Content);
                System.Console.WriteLine($"[ExtractTodo] Fallback serialization: {jsonStr.Substring(0, Math.Min(200, jsonStr.Length))}...");
                return System.Text.Json.JsonSerializer.Deserialize<Todo>(jsonStr, options);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[ExtractTodo] Fallback failed: {ex.Message}");
                return null;
            }
        }

        // Try GetContent as last resort
        return node.GetContent<Todo>();
    }

    /// <summary>
    /// Details view showing the Todo item with status, metadata, and action buttons.
    /// Enhanced with status promotion menu and assignee thumbnail.
    /// </summary>
    public static IObservable<UiControl?> Details(LayoutAreaHost host, RenderingContext _)
    {
        var todoPath = host.Hub.Address.ToString();

        // Try MeshNode stream first (works with workspace/test infrastructure)
        var meshNodeStream = host.Workspace.GetStream<MeshNode>();
        if (meshNodeStream != null)
        {
            return meshNodeStream
                .Select(nodes => nodes?.FirstOrDefault())
                .Select(node =>
                {
                    System.Console.WriteLine($"[TodoViews.Details] MeshNode stream: {node?.Path}, Content type: {node?.Content?.GetType().Name ?? "null"}");
                    var todo = ExtractTodo(node);
                    System.Console.WriteLine($"[TodoViews.Details] Extracted Todo: {todo?.Title ?? "null"}");
                    return BuildTodoDetails(host, todo);
                });
        }

        // Fallback: try Todo stream directly
        var todoStream = host.Workspace.GetStream<Todo>();
        if (todoStream != null)
        {
            return todoStream
                .Select(todos => todos?.FirstOrDefault())
                .Select(todo =>
                {
                    System.Console.WriteLine($"[TodoViews.Details] Todo stream: {todo?.Title ?? "null"}");
                    return BuildTodoDetails(host, todo);
                });
        }

        // Last resort: load directly from IMeshCatalog
        System.Console.WriteLine($"[TodoViews.Details] No stream available, loading from IMeshCatalog: {todoPath}");
        return Observable.FromAsync(async () =>
        {
            var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
            if (meshCatalog == null)
                return BuildTodoDetails(host, null);

            var node = await meshCatalog.GetNodeAsync(new Address(todoPath));
            return BuildTodoDetails(host, ExtractTodo(node));
        });
    }

    private static UiControl BuildTodoDetails(LayoutAreaHost host, Todo? todo)
    {
        if (todo == null)
            return Controls.Markdown("*Task not found*");

        var mainGrid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1));

        // Action menu positioned at top-right
        mainGrid = mainGrid.WithView(
            Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle(style => style.WithJustifyContent("flex-end").WithMarginBottom("8px"))
                .WithView(BuildTodoActionMenu(host, todo)),
            skin => skin.WithXs(12));

        // Header row with status icon, title, priority and status badges
        var statusIcon = GetStatusIcon(todo.Status);
        var priorityBadge = GetPriorityBadge(todo.Priority);
        var statusBadge = GetStatusBadge(todo.Status);

        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(style => style.WithAlignItems("center").WithGap("12px").WithMarginBottom("16px").WithFlexWrap("wrap"))
            .WithView(Controls.Icon(statusIcon).WithStyle(s => s.WithFontSize("28px")))
            .WithView(Controls.Html($"<h1 style=\"margin: 0; flex: 1; min-width: 200px;\">{System.Web.HttpUtility.HtmlEncode(todo.Title)}</h1>"))
            .WithView(Controls.Html(priorityBadge))
            .WithView(Controls.Html(statusBadge));

        mainGrid = mainGrid.WithView(headerStack, skin => skin.WithXs(12));

        // Properties grid (compact layout)
        mainGrid = mainGrid.WithView(
            BuildPropertiesGrid(host, todo),
            skin => skin.WithXs(12));

        // Description section with edit button (below properties)
        mainGrid = mainGrid.WithView(
            BuildDescriptionSection(host, todo),
            skin => skin.WithXs(12));

        // Status promotion menu - all statuses with most likely first
        mainGrid = mainGrid.WithView(
            BuildStatusPromotionMenu(host, todo),
            skin => skin.WithXs(12));

        return mainGrid;
    }

    private static UiControl BuildPropertiesGrid(LayoutAreaHost host, Todo todo)
    {
        var grid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(2));

        // Category
        grid = grid.WithView(
            Controls.Html($"<div style=\"font-size: 12px; color: var(--neutral-foreground-hint);\">Category</div><div>{todo.Category}</div>"),
            skin => skin.WithXs(6).WithMd(3));

        // Priority
        grid = grid.WithView(
            Controls.Html($"<div style=\"font-size: 12px; color: var(--neutral-foreground-hint);\">Priority</div><div>{GetPriorityLabel(todo.Priority)}</div>"),
            skin => skin.WithXs(6).WithMd(3));

        // Assignee
        var assigneeDisplay = string.IsNullOrEmpty(todo.Assignee) ? "<em>Unassigned</em>" : System.Web.HttpUtility.HtmlEncode(todo.Assignee);
        grid = grid.WithView(
            Controls.Html($"<div style=\"font-size: 12px; color: var(--neutral-foreground-hint);\">Assignee</div><div>{assigneeDisplay}</div>"),
            skin => skin.WithXs(6).WithMd(3));

        // Due Date
        var dueDateDisplay = todo.DueDate.HasValue
            ? $"{todo.DueDate.Value:MMM dd, yyyy} {GetDueDateIndicator(todo.DueDate.Value, todo.Status)}"
            : "<em>Not set</em>";
        grid = grid.WithView(
            Controls.Html($"<div style=\"font-size: 12px; color: var(--neutral-foreground-hint);\">Due Date</div><div>{dueDateDisplay}</div>"),
            skin => skin.WithXs(6).WithMd(3));

        // Created date (second row)
        grid = grid.WithView(
            Controls.Html($"<div style=\"font-size: 12px; color: var(--neutral-foreground-hint);\">Created</div><div>{todo.CreatedAt:MMM dd, yyyy}</div>"),
            skin => skin.WithXs(6).WithMd(3));

        // Completed date (if applicable)
        if (todo.CompletedAt.HasValue)
        {
            grid = grid.WithView(
                Controls.Html($"<div style=\"font-size: 12px; color: var(--neutral-foreground-hint);\">Completed</div><div>{todo.CompletedAt.Value:MMM dd, yyyy}</div>"),
                skin => skin.WithXs(6).WithMd(3));
        }

        return Controls.Stack
            .WithStyle(style => style.WithPadding("16px").WithBorder("1px solid var(--neutral-stroke-rest)").WithBorderRadius("8px").WithMarginBottom("16px"))
            .WithView(grid);
    }

    private static UiControl BuildDescriptionSection(LayoutAreaHost host, Todo todo)
    {
        var nodePath = host.Hub.Address.ToString();
        var editHref = $"/{nodePath}/Edit";

        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(style => style.WithAlignItems("center").WithJustifyContent("space-between").WithMarginBottom("8px"))
            .WithView(Controls.Html("<div style=\"font-size: 14px; font-weight: 600;\">Description</div>"))
            .WithView(Controls.Button("Edit")
                .WithIconStart(FluentIcons.Edit(IconSize.Size16))
                .WithAppearance(Appearance.Neutral)
                .WithNavigateToHref(editHref));

        UiControl descriptionContent;
        if (string.IsNullOrEmpty(todo.Description))
            descriptionContent = Controls.Html("<div style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No description provided</div>");
        else
            descriptionContent = Controls.Markdown(todo.Description);

        return Controls.Stack
            .WithStyle(style => style.WithPadding("16px").WithBorder("1px solid var(--neutral-stroke-rest)").WithBorderRadius("8px").WithMarginBottom("16px"))
            .WithView(headerStack)
            .WithView(descriptionContent);
    }

    private static UiControl BuildTodoActionMenu(LayoutAreaHost host, Todo todo)
    {
        var nodePath = host.Hub.Address.ToString();

        // Start with the trigger button (MoreHorizontal icon) - icon-only mode hides the chevron
        var menu = Controls.MenuItem("", FluentIcons.MoreHorizontal(IconSize.Size20))
            .WithAppearance(Appearance.Stealth)
            .WithIconOnly();

        // Edit option - navigates to Edit area
        var editHref = $"/{nodePath}/Edit";
        menu = menu.WithView(new NavLinkControl("Edit", FluentIcons.Edit(IconSize.Size16), editHref));

        // Delete option
        menu = menu.WithView(
            Controls.MenuItem("Delete", FluentIcons.Delete(IconSize.Size16))
                .WithClickAction(_ => { DeleteTodo(host, todo); return System.Threading.Tasks.Task.CompletedTask; }));

        // Comments option (only if comments are enabled)
        if (host.Hub.Configuration.HasComments())
        {
            var commentsHref = $"/{nodePath}/Comments";
            menu = menu.WithView(new NavLinkControl("Comments", FluentIcons.Comment(IconSize.Size16), commentsHref));
        }

        // Files option
        var filesHref = $"/{nodePath}/Files";
        menu = menu.WithView(new NavLinkControl("Files", FluentIcons.Folder(IconSize.Size16), filesHref));

        // Metadata option
        var metadataHref = $"/{nodePath}/Metadata";
        menu = menu.WithView(new NavLinkControl("Metadata", FluentIcons.Info(IconSize.Size16), metadataHref));

        // Settings option
        var settingsHref = $"/{nodePath}/Settings";
        menu = menu.WithView(new NavLinkControl("Settings", FluentIcons.Settings(IconSize.Size16), settingsHref));

        return menu;
    }

    private static void DeleteTodo(LayoutAreaHost host, Todo todo)
    {
        ShowDeleteConfirmationDialog(host, todo);
    }

    private static void ShowDeleteConfirmationDialog(LayoutAreaHost host, Todo todo)
    {
        var content = Controls.Stack
            .WithView(Controls.Html($@"
                <div style=""text-align: center; padding: 16px;"">
                    <div style=""font-size: 48px; margin-bottom: 16px;"">🗑️</div>
                    <p>Delete <strong>{System.Web.HttpUtility.HtmlEncode(todo.Title)}</strong>?</p>
                    <p style=""color: var(--neutral-foreground-hint); font-size: 14px;"">
                        You can restore it later from the Deleted view.
                    </p>
                </div>"))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle(s => s.WithJustifyContent("center").WithGap("12px"))
                .WithView(Controls.Button("Cancel").WithAppearance(Appearance.Neutral)
                    .WithClickAction(_ => { host.UpdateArea(DialogControl.DialogArea, null!); return System.Threading.Tasks.Task.CompletedTask; }))
                .WithView(Controls.Button("Delete").WithAppearance(Appearance.Accent)
                    .WithStyle(s => s.WithBackgroundColor("#dc3545"))
                    .WithClickAction(_ =>
                    {
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        return SoftDeleteTodo(host).ContinueWith(_ =>
                        {
                            // Navigate back to parent after soft delete
                            var segments = host.Hub.Address.Segments;
                            if (segments.Length > 1)
                            {
                                var parentPath = string.Join("/", segments.Take(segments.Length - 1));
                                host.Hub.ServiceProvider.GetService<MeshWeaver.Mesh.Services.INavigationService>()?.NavigateTo($"/{parentPath}");
                            }
                        });
                    })));

        host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(content, "Delete Task").WithSize("S").WithClosable(false));
    }

    private static async System.Threading.Tasks.Task SoftDeleteTodo(LayoutAreaHost host)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<MeshWeaver.Mesh.Services.IMeshCatalog>();
        var persistence = host.Hub.ServiceProvider.GetService<MeshWeaver.Mesh.Services.IPersistenceService>();
        if (meshCatalog == null || persistence == null) return;

        var todoPath = host.Hub.Address.ToString();
        var existingNode = await meshCatalog.GetNodeAsync(host.Hub.Address);
        if (existingNode == null) return;

        var deletedNode = existingNode with { State = MeshWeaver.Mesh.MeshNodeState.Deleted };
        await persistence.SaveNodeAsync(deletedNode, host.Hub.JsonSerializerOptions);
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
                Controls.Button(label)
                    .WithIconStart(icon)
                    .WithAppearance(isFirst ? Appearance.Accent : Appearance.Neutral)
                    .WithClickAction(_ =>
                    {
                        UpdateStatus(host, todo, status);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }));
            isFirst = false;
        }

        return Controls.Stack
            .WithView(Controls.Html("<h4 style=\"margin: 16px 0 8px 0; font-size: 12px; color: var(--neutral-foreground-hint); text-transform: uppercase; letter-spacing: 0.5px;\">Change Status</h4>"))
            .WithView(buttonStack);
    }

    private static IEnumerable<(string Label, string StatusId, Icon Icon)> GetOrderedStatusTransitions(string currentStatus)
    {
        // Return all statuses as possible transitions (simplified from TodoStatus which had explicit transitions)
        foreach (var status in Status.All.Where(s => s.Id != currentStatus))
        {
            yield return (status.Name, status.Id, GetIconByName("Circle"));
        }
    }

    private static string GetStatusBadge(string statusId)
    {
        var status = Status.GetById(statusId);
        var displayName = status?.DisplayName ?? statusId;
        // Use neutral styling since Status doesn't have colors
        return $"<span style=\"background: #6c757d; color: #fff; padding: 4px 12px; border-radius: 20px; font-size: 12px; font-weight: 600;\">{displayName}</span>";
    }

    private static void UpdateStatus(LayoutAreaHost host, Todo todo, string newStatus)
    {
        var newStatusObj = Status.GetById(newStatus);
        var updatedTodo = todo with
        {
            Status = newStatus,
            // Mark as completed if status is "Completed"
            CompletedAt = newStatusObj?.Id == "Completed" ? DateTime.UtcNow : null
        };

        var changeRequest = new DataChangeRequest().WithUpdates(updatedTodo);
        host.Hub.Post(changeRequest, o => o.WithTarget(host.Hub.Address));
    }

    private static Icon GetIconByName(string iconName) =>
        new Icon(FluentIcons.Provider, iconName);

    /// <summary>
    /// Thumbnail view for catalog listings - enhanced with status menu and reminder button.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var todoPath = host.Hub.Address.ToString();

        // Try MeshNode stream first (works with workspace/test infrastructure)
        var meshNodeStream = host.Workspace.GetStream<MeshNode>();
        if (meshNodeStream != null)
        {
            return meshNodeStream
                .Select(nodes => nodes?.FirstOrDefault())
                .Select(node => BuildThumbnail(host, node, todoPath));
        }

        // Fallback: try Todo stream directly
        var todoStream = host.Workspace.GetStream<Todo>();
        if (todoStream != null)
        {
            return todoStream
                .Select(todos => todos?.FirstOrDefault())
                .Select(todo => BuildThumbnail(host, null, todo, todoPath));
        }

        // Last resort: load directly from IMeshCatalog
        return Observable.FromAsync(async () =>
        {
            var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
            if (meshCatalog == null)
                return BuildThumbnail(host, null, null, todoPath);

            var node = await meshCatalog.GetNodeAsync(new Address(todoPath));
            return BuildThumbnail(host, node, todoPath);
        });
    }

    private static UiControl BuildThumbnail(LayoutAreaHost host, MeshNode? node, string hubPath)
        => BuildThumbnail(host, node, ExtractTodo(node), hubPath);

    private static UiControl BuildThumbnail(LayoutAreaHost host, MeshNode? node, Todo? todo, string hubPath)
    {
        if (todo == null)
            return Controls.Html("");

        var nodeIcon = node?.Icon ?? "/static/storage/content/ACME/Project/Todo/icon.svg";
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

        // Header row: Node icon, title, priority badge, and link
        stack = stack.WithView(Controls.Html($@"
            <div style=""display: flex; align-items: center; gap: 8px; margin-bottom: 8px;"">
                <img src=""{nodeIcon}"" style=""width: 16px; height: 16px;"" />
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

        // Status dropdown menu - primary action as main button, other statuses as sub-menu items
        var (primaryLabel, primaryStatus, primaryIcon) = GetPrimaryTransition(todo.Status);
        var statusMenu = Controls.MenuItem(primaryLabel, primaryIcon)
            .WithAppearance(Appearance.Neutral)
            .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px"))
            .WithClickAction(_ =>
            {
                UpdateStatus(host, todo, primaryStatus);
                return System.Threading.Tasks.Task.CompletedTask;
            });

        // Add all other status transitions as sub-menu items
        foreach (var (label, status, icon) in GetOrderedStatusTransitions(todo.Status))
        {
            if (status == primaryStatus) continue; // Skip primary (already the main button)
            if (status == todo.Status) continue; // Skip current status

            statusMenu = statusMenu.WithView(
                Controls.MenuItem(label, icon)
                    .WithClickAction(_ =>
                    {
                        UpdateStatus(host, todo, status);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }));
        }

        actionRow = actionRow.WithView(statusMenu);

        // Assignee display (read-only, edit via Edit dialog)
        var assigneeDisplay = string.IsNullOrEmpty(todo.Assignee) ? "Unassigned" : todo.Assignee;
        actionRow = actionRow.WithView(Controls.Html($@"
            <span style=""font-size: 12px; color: var(--neutral-foreground-hint); padding: 4px 8px;"">
                {System.Web.HttpUtility.HtmlEncode(assigneeDisplay)}
            </span>"));

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

        // Edit button - navigates to Edit area
        actionRow = actionRow.WithView(
            Controls.Button("\u270f\ufe0f")
                .WithLabel("Edit")
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px"))
                .WithNavigateToHref($"/{hubPath}/Edit"));

        // Delete button
        actionRow = actionRow.WithView(
            Controls.Button("\ud83d\uddd1\ufe0f")
                .WithLabel("Delete")
                .WithAppearance(Appearance.Neutral)
                .WithStyle(style => style.WithMinWidth("32px").WithPadding("4px 8px").WithColor("#dc3545"))
                .WithClickAction(_ =>
                {
                    DeleteTodo(host, todo);
                    return System.Threading.Tasks.Task.CompletedTask;
                }));

        stack = stack.WithView(actionRow);

        return stack;
    }

    private static (string Label, string StatusId, Icon Icon) GetPrimaryTransition(string currentStatus)
    {
        // Return next logical status transition (simplified from TodoStatus which had explicit transitions)
        return currentStatus switch
        {
            "Planning" => ("Start", "Active", FluentIcons.Play()),
            "Active" => ("Complete", "Completed", FluentIcons.CheckmarkCircle()),
            "OnHold" => ("Resume", "Active", FluentIcons.Play()),
            "Completed" => ("Reopen", "Active", FluentIcons.ArrowUndo()),
            "Cancelled" => ("Reopen", "Planning", FluentIcons.ArrowUndo()),
            _ => ("Update", "Active", FluentIcons.ArrowSync())
        };
    }

    private static bool IsOverdue(Todo todo)
    {
        // Completed or Cancelled tasks are not overdue
        if (todo.Status == "Completed" || todo.Status == "Cancelled" || !todo.DueDate.HasValue)
            return false;
        return todo.DueDate.Value.Date < DateTime.Now.Date;
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

    private static Icon GetStatusIcon(string statusId)
    {
        // Return icon based on status (Status doesn't have Icon property)
        return statusId switch
        {
            "Planning" => FluentIcons.Clipboard(),
            "Active" => FluentIcons.Play(),
            "OnHold" => FluentIcons.Pause(),
            "Completed" => FluentIcons.CheckmarkCircle(),
            "Cancelled" => FluentIcons.Dismiss(),
            _ => FluentIcons.Circle()
        };
    }

    private static string GetStatusColor(string statusId)
    {
        // Return color based on status (Status doesn't have BackgroundColor property)
        return statusId switch
        {
            "Planning" => "#ffc107",
            "Active" => "#0d6efd",
            "OnHold" => "#6c757d",
            "Completed" => "#198754",
            "Cancelled" => "#dc3545",
            _ => "#6c757d"
        };
    }

    private static string GetPriorityBadge(string priorityId)
    {
        var priority = Priority.GetById(priorityId);
        if (priority == null || priority.Id == "Medium") return ""; // Medium has no badge
        return $"<span style=\"background: {priority.BackgroundColor}; color: {priority.TextColor}; padding: 2px 6px; border-radius: 4px; font-size: 10px; font-weight: 600;\">{priority.Id.ToUpperInvariant()}</span>";
    }

    private static string GetPriorityLabel(string priorityId)
    {
        var priority = Priority.GetById(priorityId);
        return priority?.DisplayName ?? priorityId;
    }

    private static string GetDueDateIndicator(DateTime dueDate, string statusId)
    {
        // Completed or Cancelled tasks don't show due date indicators
        if (statusId == "Completed" || statusId == "Cancelled") return "";

        var today = DateTime.Now.Date;
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
