using System.Collections.Immutable;
using MeshWeaver.Todo.Domain;

namespace MeshWeaver.Todo.LayoutAreas;

/// <summary>
/// The pure projections behind the Todo areas: todos in, the rows of the area's bound list out
/// (<see cref="TodoEntry"/>). They make every data-dependent decision — which sections exist, which
/// group action a section offers, which actions a todo offers — so the template stays static and
/// these are testable without a hub.
/// </summary>
public static class TodoProjections
{
    private const string Unassigned = "Unassigned";
    private const string DefaultColor = "var(--color-fg-default)";

    // ── Areas ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>All todos: assigned ones by status, then unassigned ones by status.</summary>
    public static IEnumerable<TodoEntry> AllItems(IReadOnlyCollection<TodoItem> todos, TodoViewText x)
    {
        if (todos.Count == 0)
        {
            yield return Line("empty", x.T("todo.emptyAddHint"), TodoStyles.Muted);
            yield break;
        }

        var ordered = todos.OrderBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.CreatedAt).ToList();

        foreach (var group in ordered.Where(t => t.ResponsiblePerson != Unassigned)
                     .GroupBy(t => t.Status).OrderBy(g => (int)g.Key))
        {
            var (title, icon, action) = group.Key switch
            {
                TodoStatus.Pending => (x.T("todo.group.startAll"), "play", TodoGroupActions.Start),
                TodoStatus.InProgress => (x.T("todo.group.closeAll"), "pause", TodoGroupActions.Complete),
                TodoStatus.Completed => (x.T("todo.group.archiveAll"), "archive", TodoGroupActions.Delete),
                _ => (x.T("todo.group.deleteAll"), "trash", TodoGroupActions.Delete),
            };
            yield return Section($"assigned-{group.Key}",
                x.T("todo.sectionCount", $"{StatusIcon(group.Key)} {StatusName(group.Key, x)}", group.Count()),
                StatusColor(group.Key), "20px", title, icon, action, group);
            foreach (var todo in group)
                yield return ItemRow(todo, x, TodoStyles.IndentedCard);
        }

        foreach (var group in ordered.Where(t => t.ResponsiblePerson == Unassigned)
                     .GroupBy(t => t.Status).OrderBy(g => (int)g.Key))
        {
            yield return Section($"unassigned-{group.Key}",
                x.T("todo.sectionCount",
                    $"{StatusIcon(group.Key)} {x.T("todo.unassignedStatus", StatusName(group.Key, x))}", group.Count()),
                "var(--color-warning-fg)", "20px",
                x.T("todo.group.autoAssignAll"), "user-plus", TodoGroupActions.AutoAssign, group);
            foreach (var todo in group)
                yield return AssignRow(todo, x, TodoStyles.IndentedCard, Content(todo, x));
        }
    }

    /// <summary>Todos grouped by category, each category with its bulk action.</summary>
    public static IEnumerable<TodoEntry> ByCategory(IReadOnlyCollection<TodoItem> todos, TodoViewText x)
    {
        if (todos.Count == 0)
        {
            yield return Line("empty", x.T("todo.emptyAddHint"), TodoStyles.Muted);
            yield break;
        }

        foreach (var category in todos
                     .GroupBy(t => string.IsNullOrEmpty(t.Category) ? x.T("todo.uncategorized") : t.Category)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var all = category.ToList();
            var incomplete = all.Where(t => t.Status != TodoStatus.Completed).ToList();
            var pending = all.Where(t => t.Status == TodoStatus.Pending).ToList();
            var (title, icon, action, group) =
                incomplete.Count == 0 ? (x.T("todo.group.archiveAll"), "archive", TodoGroupActions.Delete, all)
                : pending.Count == incomplete.Count ? (x.T("todo.group.startAll"), "play", TodoGroupActions.Start, pending)
                : (x.T("todo.group.completeAll"), "check-circle", TodoGroupActions.Complete, incomplete);

            yield return Section($"category-{category.Key}",
                x.T("todo.byCategory.header", category.Key, all.Count,
                    all.Count(t => t.Status == TodoStatus.Completed),
                    all.Count(t => t.Status == TodoStatus.InProgress),
                    pending.Count),
                DefaultColor, "16px", title, icon, action, group);

            foreach (var todo in all.OrderBy(t => (int)t.Status)
                         .ThenBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.CreatedAt))
                yield return ItemRow(todo, x, TodoStyles.Card);
        }
    }

    /// <summary>Summary statistics: status breakdown, due dates, responsibility.</summary>
    public static IEnumerable<TodoEntry> Summary(IReadOnlyCollection<TodoItem> todos, TodoViewText x)
    {
        if (todos.Count == 0)
        {
            yield return Line("empty", x.T("todo.none"), TodoStyles.Muted);
            yield break;
        }

        var total = todos.Count;
        yield return Line("total", x.T("todo.summary.total", total), "margin-top: 20px; margin-bottom: 8px;");

        yield return Section("status", x.T("todo.summary.statusOverview"), DefaultColor, "12px");
        foreach (var group in todos.GroupBy(t => t.Status).OrderBy(g => (int)g.Key))
            yield return Line($"status-{group.Key}", x.T("todo.summary.statusLine", StatusIcon(group.Key),
                StatusName(group.Key, x), group.Count(), (group.Count() * 100.0 / total).ToString("F1", x.Culture)),
                TodoStyles.Stat);

        var today = DateTime.Now.Date;
        bool Open(TodoItem t) => t.Status != TodoStatus.Completed;
        yield return Section("due", x.T("todo.summary.dueInsights"), DefaultColor);
        yield return Line("overdue", x.T("todo.summary.overdue",
            todos.Count(t => t.DueDate?.Date < today && Open(t))), TodoStyles.Stat);
        yield return Line("dueToday", x.T("todo.summary.dueToday",
            todos.Count(t => t.DueDate?.Date == today && Open(t))), TodoStyles.Stat);
        yield return Line("dueWeek", x.T("todo.summary.dueWeek",
            todos.Count(t => t.DueDate?.Date > today && t.DueDate?.Date <= today.AddDays(7) && Open(t))), TodoStyles.Stat);

        yield return Section("responsibility", x.T("todo.summary.responsibility"), DefaultColor);
        yield return Line("mine", x.T("todo.summary.mine", ResponsiblePersons.GetCurrentUser(),
            todos.Count(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson))), TodoStyles.Stat);
        yield return Line("others", x.T("todo.summary.others",
            todos.Count(t => !ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson))), TodoStyles.Stat);
        yield return Line("unassigned", x.T("todo.summary.unassigned",
            todos.Count(t => t.ResponsiblePerson == Unassigned)), TodoStyles.Stat);
    }

    /// <summary>Team workload (with "assign the next unassigned task" per member) and the
    /// unassigned backlog with per-task assignment.</summary>
    public static IEnumerable<TodoEntry> Planning(IReadOnlyCollection<TodoItem> todos, TodoViewText x)
    {
        if (todos.Count == 0)
        {
            yield return Line("empty", x.T("todo.planning.empty"), TodoStyles.Muted);
            yield break;
        }

        var today = DateTime.Now.Date;
        // What can still be handed out: the SAME list the backlog section below shows. A member's
        // "Assign" takes its first entry, so a finished or cancelled todo is never the one assigned.
        var open = OpenUnassigned(todos);

        yield return Section("workload",
            x.T("todo.planning.workload", todos.Count(t => t.ResponsiblePerson != Unassigned)), DefaultColor, "20px");

        foreach (var person in todos.Where(t => t.ResponsiblePerson != Unassigned)
                     .GroupBy(t => t.ResponsiblePerson).OrderByDescending(g => g.Count()).Take(6))
        {
            var active = person.Count(t => t.Status != TodoStatus.Completed && t.Status != TodoStatus.Cancelled);
            var overdue = person.Count(t => t.DueDate?.Date < today && t.Status != TodoStatus.Completed);
            var load = active switch { <= 2 => "🟢", <= 4 => "🟡", _ => "🔴" };
            var line = x.T("todo.planning.member", load, DisplayName(person.Key, x.Localize), active)
                       + (overdue > 0 ? x.T("todo.planning.memberOverdue", overdue) : "");
            var entry = Line($"member-{person.Key}", line, TodoStyles.Stat);
            yield return open.Count == 0
                ? entry
                : entry with
                {
                    GroupTitle = x.T("todo.group.assignOne"),
                    GroupIcon = "user-plus",
                    GroupAction = TodoGroupActions.AssignFirst,
                    GroupStyle = TodoStyles.Menu,
                    Group = [.. open],
                    Person = person.Key,
                };
        }

        if (open.Count == 0)
            yield break;

        // The title counts what Auto-Assign hands out; the rows below are its first eight.
        yield return Section("backlog", x.T("todo.planning.unassigned", open.Count), DefaultColor, "16px",
            x.T("todo.group.autoAssign"), "shuffle", TodoGroupActions.AutoAssign, open);

        foreach (var task in open.Take(8))
        {
            var urgency = task.DueDate?.Date <= today ? "🚨" : task.DueDate?.Date <= today.AddDays(1) ? "⏰" : "📅";
            var line = x.T("todo.planning.task", urgency, task.Title, task.Category)
                       + (task.DueDate is { } due ? x.T("todo.planning.taskDue", due.ToString("MMM dd", x.Culture)) : "");
            yield return AssignRow(task, x, TodoStyles.Line, line) with { Icon = string.Empty };
        }
    }

    /// <summary>The current user's open tasks: urgent, due tomorrow, upcoming.</summary>
    public static IEnumerable<TodoEntry> MyTasks(IReadOnlyCollection<TodoItem> todos, TodoViewText x)
    {
        var today = DateTime.Now.Date;
        var mine = todos
            .Where(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson)
                        && t.Status is TodoStatus.Pending or TodoStatus.InProgress)
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.CreatedAt)
            .ToList();

        if (mine.Count == 0)
        {
            yield return Line("empty", x.T("todo.myTasks.empty"), TodoStyles.Success);
            yield break;
        }

        var overdue = mine.Count(t => t.DueDate?.Date < today);
        var dueToday = mine.Count(t => t.DueDate?.Date == today);
        var summary = x.T("todo.myTasks.summary", mine.Count, mine.Count(t => t.Status == TodoStatus.InProgress))
                      + (overdue > 0 ? x.T("todo.myTasks.overdue", overdue) : "")
                      + (dueToday > 0 ? x.T("todo.myTasks.dueToday", dueToday) : "");
        var pending = mine.Where(t => t.Status == TodoStatus.Pending).ToList();
        var line = Line("summary", summary, "margin-bottom: 12px;");
        yield return pending.Count == 0
            ? line
            : line with
            {
                GroupTitle = x.T("todo.group.startAll"),
                GroupIcon = "play-circle",
                GroupAction = TodoGroupActions.Start,
                GroupStyle = TodoStyles.Menu,
                Group = [.. pending],
            };

        var sections = new[]
        {
            ("urgent", x.T("todo.myTasks.urgent"), "var(--color-danger-fg)",
                mine.Where(t => t.DueDate?.Date <= today).ToList()),
            ("tomorrow", x.T("todo.myTasks.tomorrow"), "var(--color-warning-fg)",
                mine.Where(t => t.DueDate?.Date == today.AddDays(1)).ToList()),
            ("upcoming", x.T("todo.myTasks.upcoming"), DefaultColor,
                mine.Where(t => t.DueDate is null || t.DueDate.Value.Date > today.AddDays(1)).Take(10).ToList()),
        };
        foreach (var (key, title, color, tasks) in sections.Where(s => s.Item4.Count > 0))
        {
            yield return Section(key, title, color, "20px");
            foreach (var todo in tasks)
                yield return ItemRow(todo, x, TodoStyles.Card);
        }
    }

    /// <summary>Open tasks nobody is assigned to, each with its assignment menu.</summary>
    public static IEnumerable<TodoEntry> Backlog(IReadOnlyCollection<TodoItem> todos, TodoViewText x)
    {
        var open = OpenUnassigned(todos);

        if (open.Count == 0)
        {
            yield return Line("empty", x.T("todo.backlog.empty"), TodoStyles.Success);
            yield break;
        }

        yield return Section("backlog", x.T("todo.planning.unassigned", open.Count), DefaultColor, "6px",
            x.T("todo.group.autoAssignAll"), "shuffle", TodoGroupActions.AutoAssign, open);
        foreach (var todo in open)
            yield return AssignRow(todo, x, TodoStyles.Card, Content(todo, x));
    }

    /// <summary>Overdue, due-today and ongoing work, each section with its bulk action.</summary>
    public static IEnumerable<TodoEntry> TodaysFocus(IReadOnlyCollection<TodoItem> todos, TodoViewText x)
    {
        var today = DateTime.Now.Date;
        IOrderedEnumerable<TodoItem> MineFirst(IEnumerable<TodoItem> items)
            => items.OrderBy(t => ResponsiblePersons.IsCurrentUser(t.ResponsiblePerson) ? 0 : 1).ThenBy(t => t.Status);

        var overdue = MineFirst(todos.Where(t => t.DueDate?.Date < today && IsOpen(t))).ThenBy(t => t.CreatedAt).ToList();
        var dueToday = MineFirst(todos.Where(t => t.DueDate?.Date == today && IsOpen(t))).ThenBy(t => t.CreatedAt).ToList();
        var ongoing = MineFirst(todos.Where(t => t.ResponsiblePerson != Unassigned && IsOpen(t)
                                                 && (t.DueDate is null || t.DueDate.Value.Date > today)))
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.CreatedAt).ToList();

        yield return Line("summary", x.T("todo.focus.summary", overdue.Count, dueToday.Count, ongoing.Count),
            "margin-bottom: 12px;");

        if (overdue.Count + dueToday.Count + ongoing.Count == 0)
        {
            yield return Line("empty", x.T("todo.focus.empty"), TodoStyles.Success);
            yield break;
        }

        var sections = new[]
        {
            ("overdue", x.T("todo.focus.overdue"), "var(--color-danger-fg)", x.T("todo.group.startAllOverdue"),
                "play", TodoGroupActions.Start, overdue),
            ("dueToday", x.T("todo.focus.dueToday"), "var(--color-warning-fg)", x.T("todo.group.startAllToday"),
                "play", TodoGroupActions.Start, dueToday),
            ("ongoing", x.T("todo.focus.ongoing"), "var(--color-accent-fg)", x.T("todo.group.completeAll"),
                "check", TodoGroupActions.Complete, ongoing),
        };
        foreach (var (key, title, color, action, icon, code, tasks) in sections.Where(s => s.Item7.Count > 0))
        {
            yield return Section(key, x.T("todo.sectionCount", title, tasks.Count), color, "12px",
                action, icon, code, tasks);
            foreach (var todo in tasks)
                yield return ItemRow(todo, x, TodoStyles.Card);
        }
    }

    // ── Rows ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Work that is still to be done: neither completed nor cancelled. The ONE definition
    /// the planning, backlog and focus areas share, so a todo is open in all of them or in none.</summary>
    /// <param name="todo">The todo to judge.</param>
    /// <returns><c>true</c> while the todo can still be worked on.</returns>
    public static bool IsOpen(TodoItem todo) =>
        todo.Status is not (TodoStatus.Completed or TodoStatus.Cancelled);

    /// <summary>The open todos nobody is assigned to, soonest due first (then oldest) — what the
    /// backlog lists and what an assignment action hands out, in that order.</summary>
    /// <param name="todos">All todos.</param>
    /// <returns>The open unassigned todos, ordered.</returns>
    public static IReadOnlyList<TodoItem> OpenUnassigned(IEnumerable<TodoItem> todos) =>
        [.. todos.Where(t => t.ResponsiblePerson == Unassigned && IsOpen(t))
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.CreatedAt)];

    /// <summary>A section heading, optionally with a group action over <paramref name="group"/>.</summary>
    public static TodoEntry Section(string key, string title, string color, string marginTop = "16px",
        string? groupTitle = null, string groupIcon = "", string groupAction = "", IEnumerable<TodoItem>? group = null)
        => new()
        {
            Key = key,
            Heading = $"##### {title}",
            HeadingStyle = TodoStyles.Heading(color, marginTop),
            GroupTitle = groupTitle ?? string.Empty,
            GroupIcon = groupIcon,
            GroupAction = groupAction,
            GroupStyle = groupTitle is null ? TodoStyles.Hidden : TodoStyles.Menu,
            Group = group is null ? [] : [.. group],
        };

    /// <summary>A markdown line (a statistic, a summary, an empty state).</summary>
    public static TodoEntry Line(string key, string markdown, string style)
        => new() { Key = key, Heading = markdown, HeadingStyle = style };

    /// <summary>A todo card with its action menu: the primary action for its status, and the
    /// secondary actions its status offers.</summary>
    public static TodoEntry ItemRow(TodoItem todo, TodoViewText x, string cardStyle)
    {
        var (title, icon) = todo.Status switch
        {
            TodoStatus.Pending => (x.T("todo.action.start"), "play"),
            TodoStatus.InProgress => (x.T("todo.action.done"), "check"),
            TodoStatus.Completed => (x.T("todo.action.reopen"), "refresh"),
            _ => (x.T("todo.action.restore"), "refresh"),
        };
        var overdue = todo.DueDate?.Date < DateTime.Now.Date
                      && todo.Status is not (TodoStatus.Completed or TodoStatus.Cancelled);
        return Card(todo, x, cardStyle, Content(todo, x)) with
        {
            ActionsStyle = TodoStyles.Actions,
            PrimaryTitle = title,
            PrimaryIcon = icon,
            CompleteStyle = todo.Status == TodoStatus.Pending ? TodoStyles.Menu : TodoStyles.Hidden,
            PauseStyle = todo.Status == TodoStatus.InProgress ? TodoStyles.Menu : TodoStyles.Hidden,
            CancelStyle = todo.Status is TodoStatus.Pending or TodoStatus.InProgress ? TodoStyles.Menu : TodoStyles.Hidden,
            ReminderStyle = overdue ? TodoStyles.Menu : TodoStyles.Hidden,
        };
    }

    /// <summary>A todo with the assignment menu instead of its actions.</summary>
    public static TodoEntry AssignRow(TodoItem todo, TodoViewText x, string cardStyle, string content)
        => Card(todo, x, cardStyle, content) with { AssignStyle = TodoStyles.Actions };

    private static TodoEntry Card(TodoItem todo, TodoViewText x, string cardStyle, string content)
        => new()
        {
            Key = $"todo-{todo.Id}",
            Item = todo,
            ItemStyle = cardStyle,
            Icon = StatusIcon(todo.Status),
            Content = content,
        };

    /// <summary>The compact card text: title, category, responsible person, due date, description.</summary>
    public static string Content(TodoItem todo, TodoViewText x)
    {
        var text = $"**{todo.Title}**";
        if (todo.Category != "General")
            text += $" `{todo.Category}`";
        text += $" {DisplayName(todo.ResponsiblePerson, x.Localize)}";
        if (todo.DueDate is { } due)
        {
            var date = due.ToString("MMM dd", x.Culture);
            var open = todo.Status != TodoStatus.Completed;
            text += " " + (due.Date < DateTime.Now.Date && open ? x.T("todo.due.overdue", date)
                : due.Date == DateTime.Now.Date && open ? x.T("todo.due.today", date)
                : x.T("todo.due.on", date));
        }
        if (!string.IsNullOrEmpty(todo.Description))
            text += $"\n*{todo.Description}*";
        return text;
    }

    /// <summary>A person's name, marked when it is the current user.</summary>
    public static string DisplayName(string person, Func<string, object?[], string> localize)
        => ResponsiblePersons.IsCurrentUser(person) ? localize("todo.you", [person]) : person;

    /// <summary>The status glyph.</summary>
    public static string StatusIcon(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "⏳",
        TodoStatus.InProgress => "🔄",
        TodoStatus.Completed => "✅",
        TodoStatus.Cancelled => "❌",
        _ => "❓"
    };

    /// <summary>The status name in the viewer's language.</summary>
    public static string StatusName(TodoStatus status, TodoViewText x) => status switch
    {
        TodoStatus.Pending => x.T("todo.status.pending"),
        TodoStatus.InProgress => x.T("todo.status.inProgress"),
        TodoStatus.Completed => x.T("todo.status.completed"),
        TodoStatus.Cancelled => x.T("todo.status.cancelled"),
        _ => status.ToString()
    };

    private static string StatusColor(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "var(--color-warning-fg)",
        TodoStatus.InProgress => "var(--color-accent-fg)",
        TodoStatus.Completed => "var(--color-success-fg)",
        TodoStatus.Cancelled => "var(--color-danger-fg)",
        _ => DefaultColor
    };
}
