// <meshweaver>
// Id: TodoLayoutAreas
// DisplayName: Todo Views
// </meshweaver>

using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

/// <summary>
/// Custom views for Todo items.
/// </summary>
public static class TodoLayoutAreas
{
    /// <summary>
    /// The node's Todo content. <c>ContentAs</c> is the framework accessor that every branch of the
    /// hand-rolled extractor this replaced was trying to be — and it is strictly better at each:
    ///
    /// <list type="bullet">
    /// <item>the already-typed value — same;</item>
    /// <item>a degraded <c>JsonElement</c> (and <c>JsonNode</c>, which the hand-rolled version
    ///   missed: application code builds content as a <c>JsonObject</c> and a change notification
    ///   forwards that shape verbatim);</item>
    /// <item>a same-short-named <c>Todo</c> from another build — every recompile of this NodeType
    ///   mints a new collectible assembly, so "the same" record has a different CLR identity per
    ///   build. The hand-rolled version reached for this with a <c>GetType().Name</c> probe and an
    ///   UNGUARDED <c>JsonSerializer.Serialize(object)</c>, which routes through the polymorphic
    ///   converter's <c>GetOrAddType</c> and adopts the foreign type into this hub's registry as a
    ///   read side effect; the accessor serializes as the concrete runtime type, whose
    ///   collectible-assembly guard formats the discriminator WITHOUT registering.</item>
    /// </list>
    ///
    /// <para>It also reads with the HUB's options (the registry behind them is what resolves a
    /// <c>$type</c>) instead of a locally-invented <c>JsonSerializerOptions</c>, and it diagnoses
    /// through the logger instead of a bare <c>catch</c> plus <c>Console.WriteLine</c>.</para>
    /// </summary>
    private static Todo? ExtractTodo(LayoutAreaHost host, MeshNode? node) =>
        node.ContentAs<Todo>(host.Hub.JsonSerializerOptions);

    /// <summary>
    /// Details view showing the Todo item with status, metadata, and action buttons.
    /// Enhanced with status promotion menu and assignee thumbnail.
    /// </summary>
    public static IObservable<UiControl?> Details(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetStream<MeshNode>()
            .Select(nodes => nodes?.FirstOrDefault())
            .Select(node => BuildTodoDetails(host, ExtractTodo(host, node)));
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
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle(style => style.WithAlignItems("center").WithGap("12px").WithMarginBottom("16px").WithFlexWrap("wrap"))
            .WithView(Controls.Icon(statusIcon).WithStyle(s => s.WithFontSize("28px")))
            .WithView(Controls.H1(todo.Title).WithStyle("margin: 0; flex: 1; min-width: 200px;"));
        if (GetPriorityBadge(todo.Priority) is { } priorityBadge)
            headerStack = headerStack.WithView(priorityBadge);
        headerStack = headerStack.WithView(GetStatusBadge(todo.Status));

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
        grid = grid.WithView(Fact("Category", Controls.Body(todo.Category)), skin => skin.WithXs(6).WithMd(3));

        // Priority
        grid = grid.WithView(Fact("Priority", Controls.Body(GetPriorityLabel(todo.Priority))), skin => skin.WithXs(6).WithMd(3));

        // Assignee
        grid = grid.WithView(
            Fact("Assignee", string.IsNullOrEmpty(todo.Assignee) ? Controls.Hint("Unassigned") : Controls.Body(todo.Assignee)),
            skin => skin.WithXs(6).WithMd(3));

        // Due Date
        UiControl dueDate = Controls.Hint("Not set");
        if (todo.DueDate.HasValue)
        {
            var due = Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(6)
                .WithView(Controls.Body($"{todo.DueDate.Value:MMM dd, yyyy}"));
            if (GetDueDateIndicator(todo.DueDate.Value, todo.Status) is { } indicator)
                due = due.WithView(indicator);
            dueDate = due;
        }
        grid = grid.WithView(Fact("Due Date", dueDate), skin => skin.WithXs(6).WithMd(3));

        // Created date (second row)
        grid = grid.WithView(Fact("Created", Controls.Body($"{todo.CreatedAt:MMM dd, yyyy}")), skin => skin.WithXs(6).WithMd(3));

        // Completed date (if applicable)
        if (todo.CompletedAt.HasValue)
        {
            grid = grid.WithView(
                Fact("Completed", Controls.Body($"{todo.CompletedAt.Value:MMM dd, yyyy}")),
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
            .WithView(Controls.Subject("Description"))
            .WithView(Controls.Button("Edit")
                .WithIconStart(FluentIcons.Edit(IconSize.Size16))
                .WithAppearance(Appearance.Neutral)
                .WithNavigateToHref(editHref));

        UiControl descriptionContent;
        if (string.IsNullOrEmpty(todo.Description))
            descriptionContent = Controls.Hint("No description provided");
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
            .WithView(Controls.Stack
                .WithVerticalGap(8)
                .WithStyle(s => s.WithAlignItems("center").WithPadding("16px"))
                .WithView(Controls.Icon(FluentIcons.Delete(IconSize.Size48)))
                .WithView(Controls.Body($"Delete “{todo.Title}”?"))
                .WithView(Controls.Hint("You can restore it later from the Deleted view.")))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle(s => s.WithJustifyContent("center").WithGap("12px"))
                .WithView(Controls.Button("Cancel").WithAppearance(Appearance.Neutral)
                    .WithClickAction(_ => { host.UpdateArea(DialogControl.DialogArea, null!); return System.Threading.Tasks.Task.CompletedTask; }))
                .WithView(Controls.Button("Delete").WithAppearance(Appearance.Accent)
                    .WithStyle(s => s.WithBackgroundColor("#dc3545"))
                    .WithClickAction(ctx =>
                    {
                        host.UpdateArea(DialogControl.DialogArea, null!);
                        // Subscribe — no await on hub round-trip. Navigation happens
                        // on the success leg once the soft-delete commit lands.
                        SoftDeleteTodo(host, ctx);
                        return System.Threading.Tasks.Task.CompletedTask;
                    })));

        host.UpdateArea(DialogControl.DialogArea, Controls.Dialog(content, "Delete Task").WithSize("S").WithClosable(false));
    }

    // No async — fully reactive. The workspace stream feeds into UpdateNode via
    // SelectMany; Subscribe drives the side effect. Errors propagate to OnError.
    // See Doc/Architecture/AsynchronousCalls.md.
    private static void SoftDeleteTodo(LayoutAreaHost host, MeshWeaver.Layout.UiActionContext ctx)
    {
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService == null) return;

        var meshNodeStream = host.Workspace.GetStream<MeshNode>();
        if (meshNodeStream == null) return;

        meshNodeStream
            .Select(nodes => nodes?.FirstOrDefault())
            .Where(n => n is not null)
            .Take(1)
            .SelectMany(node => meshService.UpdateNode(
                node! with { State = MeshWeaver.Mesh.MeshNodeState.Deleted }))
            .Subscribe(
                _ =>
                {
                    var segments = host.Hub.Address.Segments;
                    if (segments.Length > 1)
                    {
                        var parentPath = string.Join("/", segments.Take(segments.Length - 1));
                        ctx.NavigateTo($"/{parentPath}");
                    }
                },
                _ => { /* dialog already closed; surfacing here would require a re-open */ });
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
            .WithView(Controls.Subject("Change Status").WithColor(Controls.HintColor).WithStyle("margin: 16px 0 8px 0;"))
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

    private static UiControl GetStatusBadge(string statusId)
    {
        var status = Status.GetById(statusId);
        // Neutral appearance: Status carries no colour of its own.
        return Controls.Badge(status?.DisplayName ?? statusId).WithAppearance("Neutral");
    }

    /// <summary>A label/value fact: the label as a subject line, the value underneath.</summary>
    private static UiControl Fact(string label, UiControl value) =>
        Controls.Stack.WithVerticalGap(2)
            .WithView(Controls.Subject(label).WithColor(Controls.HintColor))
            .WithView(value);

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

        return host.Workspace.GetStream<MeshNode>()
            .Select(nodes => nodes?.FirstOrDefault())
            .Select(node => BuildThumbnail(host, node, todoPath));
    }

    private static UiControl BuildThumbnail(LayoutAreaHost host, MeshNode? node, string hubPath)
        => BuildThumbnail(host, node, ExtractTodo(host, node), hubPath);

    private static UiControl BuildThumbnail(LayoutAreaHost host, MeshNode? node, Todo? todo, string hubPath)
    {
        if (todo == null)
            return Controls.Stack;

        var nodeIcon = node?.Icon ?? "";
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
        var titleRow = Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(8)
            .WithStyle(s => s.WithAlignItems("center").WithMarginBottom("8px"))
            .WithView(Controls.Image(nodeIcon, "").WithSize(16).WithNodePath(hubPath))
            .WithView(Controls.Link(todo.Title, $"/{hubPath}").WithStyle("font-weight: 600; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;"));
        if (GetPriorityBadge(todo.Priority) is { } priorityBadge)
            titleRow = titleRow.WithView(priorityBadge);
        stack = stack.WithView(titleRow);

        // Category and due date info
        var infoRow = Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(6)
            .WithStyle(s => s.WithAlignItems("center").WithMarginBottom("8px"))
            .WithView(Controls.Hint(todo.Category))
            .WithView(Controls.Spacer);
        if (todo.DueDate.HasValue)
        {
            infoRow = infoRow.WithView(Controls.Hint($"Due: {todo.DueDate.Value:MMM dd}"));
            if (GetDueDateIndicator(todo.DueDate.Value, todo.Status) is { } indicator)
                infoRow = infoRow.WithView(indicator);
        }
        stack = stack.WithView(infoRow);

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
        actionRow = actionRow.WithView(Controls.Hint(assigneeDisplay).WithStyle("padding: 4px 8px;"));

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

    private static UiControl? GetPriorityBadge(string priorityId)
    {
        var priority = Priority.GetById(priorityId);
        if (priority == null || priority.Id == "Medium") return null; // Medium has no badge
        // The priority's own colours are content (the Priority record carries them).
        return Controls.Badge(priority.Id.ToUpperInvariant())
            .WithBackgroundColor(priority.BackgroundColor)
            .WithColor(priority.TextColor);
    }

    private static string GetPriorityLabel(string priorityId)
    {
        var priority = Priority.GetById(priorityId);
        return priority?.DisplayName ?? priorityId;
    }

    private static UiControl? GetDueDateIndicator(DateTime dueDate, string statusId)
    {
        // Completed or Cancelled tasks don't show due date indicators
        if (statusId == "Completed" || statusId == "Cancelled") return null;

        var today = DateTime.Now.Date;
        var dueDateOnly = dueDate.Date;

        // The portal's status tokens, with a Fluent default as the fallback.
        if (dueDateOnly < today)
            return Controls.Badge("OVERDUE")
                .WithBackgroundColor("var(--error, #d13438)").WithColor("var(--foreground-on-accent-rest, white)");
        if (dueDateOnly == today)
            return Controls.Badge("TODAY")
                .WithBackgroundColor("var(--warning-fill-rest, #fde300)").WithColor("var(--warning-foreground, #242424)");
        if (dueDateOnly == today.AddDays(1))
            return Controls.Badge("Tomorrow").WithAppearance("Neutral");

        return null;
    }
}
