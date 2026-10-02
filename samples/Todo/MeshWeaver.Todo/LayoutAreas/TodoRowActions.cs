using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.ShortGuid;
using MeshWeaver.Todo.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Todo.LayoutAreas;

/// <summary>
/// The actions of the Todo row template — ROW-SCOPED (Doc/GUI/DataBinding → "Row-scoped actions"):
/// each button is declared once, and each click reads the row it was raised from with
/// <c>ctx.RowAs&lt;TodoEntry&gt;()</c> — the row as the person saw it, never one re-read by
/// position. A click with no row acts on nothing.
///
/// <para>Writes go to the hub that owns the todo data source (the area's own hub) through the two
/// write helpers on <see cref="TodoLayoutAreas"/> (<see cref="TodoLayoutAreas.Update"/>,
/// <see cref="TodoLayoutAreas.Delete"/>) — todos are data-source entities, not mesh nodes.</para>
/// </summary>
public static class TodoRowActions
{
    /// <summary>The width of every Todo menu button.</summary>
    public const string MenuWidth = "150px";

    /// <summary>The primary action of the clicked row's todo, chosen by its status as rendered.</summary>
    public static Task OnPrimary(UiActionContext ctx, LayoutAreaHost host)
    {
        if (ctx.RowAs<TodoEntry>() is { Item: { } todo })
            SetStatus(host, todo, todo.Status switch
            {
                TodoStatus.Pending => TodoStatus.InProgress,
                TodoStatus.InProgress => TodoStatus.Completed,
                TodoStatus.Completed => TodoStatus.InProgress,
                _ => TodoStatus.Pending,
            });
        return Task.CompletedTask;
    }

    /// <summary>Moves the clicked row's todo to <paramref name="status"/>.</summary>
    public static Task OnSetStatus(UiActionContext ctx, LayoutAreaHost host, TodoStatus status)
    {
        if (ctx.RowAs<TodoEntry>() is { Item: { } todo })
            SetStatus(host, todo, status);
        return Task.CompletedTask;
    }

    /// <summary>Deletes the clicked row's todo.</summary>
    public static Task OnDelete(UiActionContext ctx, LayoutAreaHost host)
    {
        if (ctx.RowAs<TodoEntry>() is { Item: { } todo })
            TodoLayoutAreas.Delete(host, [todo]);
        return Task.CompletedTask;
    }

    /// <summary>Assigns the clicked row's todo to <paramref name="person"/>.</summary>
    public static Task OnAssign(UiActionContext ctx, LayoutAreaHost host, string person)
    {
        if (ctx.RowAs<TodoEntry>() is { Item: { } todo })
            TodoLayoutAreas.Update(host, [todo with { ResponsiblePerson = person, UpdatedAt = DateTime.UtcNow }]);
        return Task.CompletedTask;
    }

    /// <summary>Opens the edit dialog for the clicked row's todo.</summary>
    public static Task OnEdit(UiActionContext ctx, LayoutAreaHost host)
    {
        if (ctx.RowAs<TodoEntry>() is { Item: { } todo })
            ShowEditDialog(host, todo);
        return Task.CompletedTask;
    }

    /// <summary>Opens the overdue-reminder dialog for the clicked row's todo.</summary>
    public static Task OnReminder(UiActionContext ctx, LayoutAreaHost host)
    {
        if (ctx.RowAs<TodoEntry>() is { Item: { DueDate: not null } todo })
            ShowReminderDialog(host, todo);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The clicked heading row's group action, over the group AS RENDERED (<see cref="TodoEntry.Group"/>).
    /// An unknown action is logged and does nothing — never a default that means something.
    /// </summary>
    public static Task OnGroup(UiActionContext ctx, LayoutAreaHost host)
    {
        if (ctx.RowAs<TodoEntry>() is not { GroupAction.Length: > 0 } row || row.Group.IsEmpty)
            return Task.CompletedTask;

        switch (row.GroupAction)
        {
            case TodoGroupActions.Start:
                SetStatus(host, row.Group, TodoStatus.InProgress);
                break;
            case TodoGroupActions.Complete:
                SetStatus(host, row.Group, TodoStatus.Completed);
                break;
            case TodoGroupActions.Delete:
                TodoLayoutAreas.Delete(host, row.Group);
                break;
            case TodoGroupActions.AutoAssign:
                var team = ResponsiblePersons.AvailablePersons;
                TodoLayoutAreas.Update(host, row.Group.Select((todo, i) =>
                    todo with { ResponsiblePerson = team[i % team.Length], UpdatedAt = DateTime.UtcNow }));
                break;
            case TodoGroupActions.AssignFirst when row.Person is { Length: > 0 } person:
                TodoLayoutAreas.Update(host,
                    [row.Group[0] with { ResponsiblePerson = person, UpdatedAt = DateTime.UtcNow }]);
                break;
            default:
                host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TodoRowActions))
                    .LogWarning("Todo row {Key} carries group action {Action}, which no handler claims; nothing done.",
                        row.Key, row.GroupAction);
                break;
        }
        return Task.CompletedTask;
    }

    private static void SetStatus(LayoutAreaHost host, TodoItem todo, TodoStatus status)
        => SetStatus(host, [todo], status);

    private static void SetStatus(LayoutAreaHost host, IEnumerable<TodoItem> todos, TodoStatus status)
        => TodoLayoutAreas.Update(host,
            todos.Select(todo => todo with { Status = status, UpdatedAt = DateTime.UtcNow }));

    // ── Dialogs ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates a todo and opens the dialog editing it; Cancel deletes it again.</summary>
    public static void ShowNewTodoDialog(LayoutAreaHost host)
    {
        var todo = new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "",
            Description = "",
            Status = TodoStatus.Pending,
            Category = "General",
            ResponsiblePerson = ResponsiblePersons.GetCurrentUser(),
            DueDate = DateTime.Now.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        const string dataId = "NewTodoData";

        var form = Controls.Stack
            .WithView(Controls.H5(host.Localize("todo.dialog.create"))
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(host.Edit(todo, dataId)
                .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), dataId)
            .WithView(Controls.Stack
                .WithView(Controls.Button(host.Localize("todo.dialog.save"))
                    .WithClickAction(_ =>
                    {
                        // The editor saves as you type — Save only closes.
                        host.UpdateArea(DialogControl.DialogArea, null);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button(host.Localize("todo.dialog.cancel"))
                    .WithClickAction(_ =>
                    {
                        TodoLayoutAreas.Delete(host, [todo]);
                        host.UpdateArea(DialogControl.DialogArea, null);
                        return Task.CompletedTask;
                    }))
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(10)
                .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
            .WithVerticalGap(15)
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

        host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(form, host.Localize("todo.dialog.create")).WithSize("M").WithClosable(false));
    }

    /// <summary>Opens the dialog editing <paramref name="todo"/> (the editor saves as you type).</summary>
    public static void ShowEditDialog(LayoutAreaHost host, TodoItem todo)
    {
        var dataId = $"EditTodoData_{todo.Id}";
        var form = Controls.Stack
            .WithView(Controls.H5(host.Localize("todo.dialog.edit"))
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(host.Edit(todo, dataId)
                .WithStyle(style => style.WithWidth("100%").WithDisplay("block")), dataId)
            .WithView(Controls.Stack
                .WithView(Controls.Button(host.Localize("todo.dialog.done"))
                    .WithClickAction(_ =>
                    {
                        host.UpdateArea(DialogControl.DialogArea, null);
                        return Task.CompletedTask;
                    }))
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(10)
                .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
            .WithVerticalGap(15)
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

        host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(form, host.Localize("todo.dialog.edit")).WithSize("M").WithClosable(false));
    }

    /// <summary>Opens the overdue-reminder dialog for <paramref name="todo"/>.</summary>
    public static void ShowReminderDialog(LayoutAreaHost host, TodoItem todo)
    {
        var due = todo.DueDate ?? DateTime.Now;
        var dueText = due.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var body = host.Localize("todo.reminder.body", todo.ResponsiblePerson, todo.Title, todo.Description, dueText);
        var form = Controls.Stack
            .WithView(Controls.H5(host.Localize("todo.reminder.heading"))
                .WithStyle(style => style.WithWidth("100%").WithTextAlign("center")))
            .WithView(Controls.Markdown(host.Localize("todo.reminder.task", todo.Title)))
            .WithView(Controls.Markdown(host.Localize("todo.reminder.assigned",
                TodoProjections.DisplayName(todo.ResponsiblePerson, host.Localize))))
            .WithView(Controls.Markdown(host.Localize("todo.reminder.due", dueText, Math.Abs((DateTime.Now - due).Days)))
                .WithStyle(style => style.WithColor("var(--color-danger-fg)")))
            .WithView(Controls.Markdown(host.Localize("todo.reminder.emailText")))
            .WithView(Controls.Markdown(string.Join("\n", body.Split('\n').Select(l => "> " + l))))
            .WithView(Controls.Stack
                .WithView(Controls.Button(host.Localize("todo.action.sendReminder"))
                    .WithClickAction(_ =>
                    {
                        // A sample: a real application would send the reminder here.
                        host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TodoRowActions))
                            .LogInformation("Overdue reminder for todo {Id} to {Person}", todo.Id, todo.ResponsiblePerson);
                        host.UpdateArea(DialogControl.DialogArea, null);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button(host.Localize("todo.dialog.cancel"))
                    .WithClickAction(_ =>
                    {
                        host.UpdateArea(DialogControl.DialogArea, null);
                        return Task.CompletedTask;
                    }))
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(10)
                .WithStyle(style => style.WithJustifyContent("center").WithWidth("100%")))
            .WithStyle(style => style.WithWidth("100%").WithDisplay("block").WithMargin("0 auto"));

        host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(form, host.Localize("todo.reminder.title")).WithSize("M").WithClosable(false));
    }
}
