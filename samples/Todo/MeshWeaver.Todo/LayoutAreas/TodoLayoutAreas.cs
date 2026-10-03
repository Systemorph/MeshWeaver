using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Todo.Domain;

namespace MeshWeaver.Todo.LayoutAreas;

/// <summary>
/// Layout areas for the Todo application.
///
/// <para>Every area is a TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): it returns
/// its whole control tree on the first render — a title, the "Add New Todo" action, and ONE bound
/// list — and the todos arrive later, as rows (<see cref="TodoEntry"/>) a pure projection computes
/// from the data source. Nothing on the hub waits for the todos before the page renders.</para>
///
/// <para>Each row's buttons are declared once in the row template and are ROW-SCOPED
/// (Doc/GUI/DataBinding → "Row-scoped actions"): a click carries the row it was raised from, so
/// clicking "Start" in row k starts the todo shown in row k — also after the list has changed
/// underneath (<see cref="TodoRowActions"/>).</para>
/// </summary>
public static class TodoLayoutAreas
{
    /// <summary>The data id of every Todo area's bound row list.</summary>
    public const string EntriesId = "todoEntries";

    /// <summary>
    /// All todos, grouped by status, with per-item and per-group actions.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>The area's template; its rows bind to the live todo list.</returns>
    [Display(GroupName = "2. Team Overview", Order = 2)]
    public static UiControl AllItems(LayoutAreaHost host, RenderingContext context)
        => Page(host, host.Localize("todo.allItems.title"), Typography.H4, withAdd: true,
            Feed(host, TodoProjections.AllItems));

    /// <summary>
    /// Todos grouped by category, with category-level and item-level actions.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>The area's template; its rows bind to the live todo list.</returns>
    [Display(GroupName = "2. Team Overview", Order = 3)]
    public static UiControl TodosByCategory(LayoutAreaHost host, RenderingContext context)
        => Page(host, host.Localize("todo.byCategory.title"), Typography.H2, withAdd: true,
            Feed(host, TodoProjections.ByCategory));

    /// <summary>
    /// Summary statistics.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>The area's template; its lines bind to the live todo list.</returns>
    [Display(GroupName = "2. Team Overview", Order = 1)]
    public static UiControl Summary(LayoutAreaHost host, RenderingContext context)
        => Page(host, host.Localize("todo.summary.title"), Typography.H4, withAdd: false,
            Feed(host, TodoProjections.Summary));

    /// <summary>
    /// Team workload and task assignment.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>The area's template; its rows bind to the live todo list.</returns>
    [Display(GroupName = "3. Planning")]
    public static UiControl Planning(LayoutAreaHost host, RenderingContext context)
        => Page(host, host.Localize("todo.planning.title"), Typography.H2, withAdd: true,
            Feed(host, TodoProjections.Planning));

    /// <summary>
    /// The current user's active tasks, by urgency.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>The area's template; its rows bind to the live todo list.</returns>
    [Display(GroupName = "1. My Overview")]
    public static UiControl MyTasks(LayoutAreaHost host, RenderingContext context)
        => Page(host, host.Localize("todo.myTasks.title", ResponsiblePersons.GetCurrentUser()), Typography.H2, withAdd: true,
            Feed(host, TodoProjections.MyTasks));

    /// <summary>
    /// Open tasks nobody is assigned to.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>The area's template; its rows bind to the live todo list.</returns>
    [Display(GroupName = "3. Planning")]
    public static UiControl Backlog(LayoutAreaHost host, RenderingContext context)
        => Page(host, host.Localize("todo.backlog.title"), Typography.H2, withAdd: true,
            Feed(host, TodoProjections.Backlog));

    /// <summary>
    /// Overdue, due-today and ongoing work.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>The area's template; its rows bind to the live todo list.</returns>
    [Display(GroupName = "2. Team Overview", Order = 0)]
    public static UiControl TodaysFocus(LayoutAreaHost host, RenderingContext context)
        => Page(host, host.Localize("todo.focus.title"), Typography.H4, withAdd: false,
            Feed(host, TodoProjections.TodaysFocus));

    // ── Template ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The page every Todo area renders AT ONCE: its title, the "Add New Todo" action, and the row
    /// list bound to <paramref name="entries"/>. Reads nothing.
    /// </summary>
    internal static LayoutGridControl Page(LayoutAreaHost host, string title, Typography typo, bool withAdd,
        IObservable<IEnumerable<TodoEntry>> entries)
    {
        var grid = Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1))
            .WithView(Controls.Label(title).WithTypo(typo).WithStyle(style => style.WithMarginBottom("6px").WithColor("var(--color-fg-default)")),
                skin => withAdd ? skin.WithXs(12).WithSm(9).WithMd(10) : skin.WithXs(12));
        if (withAdd)
            grid = grid.WithView(Controls.MenuItem(host.Localize("todo.addNew"), "plus")
                    .WithClickAction(_ => { TodoRowActions.ShowNewTodoDialog(host); return Task.CompletedTask; })
                    .WithWidth(TodoRowActions.MenuWidth)
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(TodoStyles.Menu),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));
        var rowView = RowView(host);
        return grid.WithView(entries.BindMany(EntriesId, _ => rowView), skin => skin.WithXs(12));
    }

    /// <summary>
    /// The ONE row template: a heading part (markdown + group action) and an item part (card +
    /// action menu, or assignment menu). Every value is a relative pointer into the row; every
    /// button is row-scoped.
    /// </summary>
    internal static LayoutGridControl RowView(LayoutAreaHost host)
    {
        var actions = Styled(Controls.Stack.WithView(ItemMenu(host)), nameof(TodoEntry.ActionsStyle));
        var assign = Styled(Controls.Stack.WithView(AssignMenu(host)), nameof(TodoEntry.AssignStyle));

        return Controls.LayoutGrid.WithSkin(skin => skin.WithSpacing(-1))
            .WithView(Styled(Controls.Markdown(P(nameof(TodoEntry.Heading))), nameof(TodoEntry.HeadingStyle)),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(Controls.MenuItem(P(nameof(TodoEntry.GroupTitle)), P(nameof(TodoEntry.GroupIcon)))
                    .WithClickAction(ctx => TodoRowActions.OnGroup(ctx, host))
                    .WithWidth(TodoRowActions.MenuWidth)
                    .WithAppearance(Appearance.Neutral)
                    .WithStyle(P(nameof(TodoEntry.GroupStyle))),
                skin => skin.WithXs(12).WithSm(3).WithMd(2))
            .WithView(Styled(Controls.Stack
                    .WithView(Controls.Markdown(P(nameof(TodoEntry.Icon))))
                    .WithView(Controls.Markdown(P(nameof(TodoEntry.Content))))
                    .WithOrientation(Orientation.Horizontal)
                    .WithHorizontalGap(12), nameof(TodoEntry.ItemStyle)),
                skin => skin.WithXs(12).WithSm(9).WithMd(10))
            .WithView(Controls.Stack.WithView(actions).WithView(assign),
                skin => skin.WithXs(12).WithSm(3).WithMd(2));
    }

    /// <summary>The item action menu: the status-dependent primary action, and the secondary
    /// actions, each shown only where its bound style says so.</summary>
    private static MenuItemControl ItemMenu(LayoutAreaHost host)
        => Menu(Controls.MenuItem(P(nameof(TodoEntry.PrimaryTitle)), P(nameof(TodoEntry.PrimaryIcon))),
                ctx => TodoRowActions.OnPrimary(ctx, host), TodoStyles.Menu)
            .WithView(Menu(Controls.MenuItem(host.Localize("todo.action.edit"), "edit"),
                ctx => TodoRowActions.OnEdit(ctx, host), TodoStyles.Menu))
            .WithView(Menu(Controls.MenuItem(host.Localize("todo.action.complete"), "check"),
                ctx => TodoRowActions.OnSetStatus(ctx, host, TodoStatus.Completed), P(nameof(TodoEntry.CompleteStyle))))
            .WithView(Menu(Controls.MenuItem(host.Localize("todo.action.pause"), "pause"),
                ctx => TodoRowActions.OnSetStatus(ctx, host, TodoStatus.Pending), P(nameof(TodoEntry.PauseStyle))))
            .WithView(Menu(Controls.MenuItem(host.Localize("todo.action.cancel"), "close"),
                ctx => TodoRowActions.OnSetStatus(ctx, host, TodoStatus.Cancelled), P(nameof(TodoEntry.CancelStyle))))
            .WithView(Menu(Controls.MenuItem(host.Localize("todo.action.delete"), "trash"),
                ctx => TodoRowActions.OnDelete(ctx, host), TodoStyles.Menu))
            .WithView(Menu(Controls.MenuItem(host.Localize("todo.action.sendReminder"), "send"),
                ctx => TodoRowActions.OnReminder(ctx, host), P(nameof(TodoEntry.ReminderStyle))));

    /// <summary>The assignment menu: one entry per team member, each assigning the row's todo.</summary>
    private static MenuItemControl AssignMenu(LayoutAreaHost host)
        => ResponsiblePersons.AvailablePersons.Take(5).Aggregate(
            Controls.MenuItem(host.Localize("todo.action.assign"), "user-plus")
                .WithWidth(TodoRowActions.MenuWidth)
                .WithAppearance(Appearance.Neutral)
                .WithStyle(TodoStyles.Menu),
            (menu, person) => menu.WithView(Menu(
                Controls.MenuItem(TodoProjections.DisplayName(person, host.Localize), "user"),
                ctx => TodoRowActions.OnAssign(ctx, host, person), TodoStyles.Menu)));

    private static MenuItemControl Menu(MenuItemControl item, Func<UiActionContext, Task> onClick, object style)
        => item.WithClickAction(onClick)
            .WithWidth(TodoRowActions.MenuWidth)
            .WithAppearance(Appearance.Neutral)
            .WithStyle(style);

    /// <summary><paramref name="control"/> with its style BOUND to a row property: a part the row
    /// does not have is hidden by it, so the template's shape never depends on data.</summary>
    private static T Styled<T>(T control, string styleProperty) where T : UiControl
        => (T)(control with { Style = P(styleProperty) });

    /// <summary>A relative pointer to a <see cref="TodoEntry"/> property (camelCase, as the data
    /// section stores it).</summary>
    private static Data.JsonPointerReference P(string property)
        => new(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property));

    // ── Feed ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The feed half: the live todos, projected into rows by <paramref name="project"/> in the
    /// viewer's language. Builds no control.
    /// </summary>
    internal static IObservable<IEnumerable<TodoEntry>> Feed(LayoutAreaHost host,
        Func<IReadOnlyCollection<TodoItem>, TodoViewText, IEnumerable<TodoEntry>> project)
    {
        var text = TodoViewText.For(host);
        var todos = host.Workspace.GetStream<TodoItem>()
            ?? throw new InvalidOperationException($"No data source for {nameof(TodoItem)} on {host.Hub.Address}.");
        return todos.Select(items => project((IReadOnlyCollection<TodoItem>?)items ?? [], text));
    }

    // ── Writes ─────────────────────────────────────────────────────────────────────────────────
    // The ONLY two places the sample names the data-plane change message. Todos are data-source
    // entities of this hub, not mesh nodes, so there is no node stream to Update; the change is
    // POSTED to the owning hub rather than applied on its workspace directly, so it passes the
    // hub's delivery gate and change validators as the clicking user.

    /// <summary>Writes <paramref name="todos"/> to the hub that owns the todo data source.</summary>
    /// <param name="host">The area host; its hub owns the data source.</param>
    /// <param name="todos">The todos as they should be stored.</param>
    internal static void Update(LayoutAreaHost host, IEnumerable<TodoItem> todos)
        => host.Hub.Post(new DataChangeRequest().WithUpdates(todos), o => o.WithTarget(host.Hub.Address));

    /// <summary>Deletes <paramref name="todos"/> on the hub that owns the todo data source.</summary>
    /// <param name="host">The area host; its hub owns the data source.</param>
    /// <param name="todos">The todos to delete.</param>
    internal static void Delete(LayoutAreaHost host, IEnumerable<TodoItem> todos)
        => host.Hub.Post(new DataChangeRequest().WithDeletions(todos), o => o.WithTarget(host.Hub.Address));
}

/// <summary>
/// The viewer's language for the Todo projections: <see cref="T"/> resolves a catalog key, and
/// <see cref="Culture"/> formats dates — never the ambient culture (Doc/Architecture/Localization).
/// </summary>
/// <param name="Localize">Resolves a key with positional arguments.</param>
/// <param name="Culture">The viewer's culture for date formatting.</param>
public sealed record TodoViewText(Func<string, object?[], string> Localize, CultureInfo Culture)
{
    /// <summary>The text for <paramref name="key"/> in the viewer's language.</summary>
    public string T(string key, params object?[] args) => Localize(key, args);

    /// <summary>The viewer's text for <paramref name="host"/>.</summary>
    public static TodoViewText For(LayoutAreaHost host)
        => new(host.Localize, CultureInfo.GetCultureInfo(host.ViewerLocale()));
}
