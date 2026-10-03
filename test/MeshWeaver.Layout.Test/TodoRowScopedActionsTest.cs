using System.Collections;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using MeshWeaver.Todo.Domain;
using MeshWeaver.Todo.LayoutAreas;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The Todo sample's areas are TEMPLATES (Doc/GUI/DataBinding → "Templates first, data later") whose
/// per-row buttons are ROW-SCOPED (→ "Row-scoped actions"). Before, each area waited for the todos on
/// the hub and baked one button per todo with the todo captured in its closure; now the page renders
/// at once, the rows bind to a projection, and each button exists ONCE in the row template — the click
/// carries the row it came from. These tests pin both halves: the page renders with no data, and
/// clicking a button in row k acts on the todo of row k, for every k, also after the list has changed.
/// </summary>
public class TodoRowScopedActionsTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string NoDataArea = "NoData";
    private const string Assignee = "Jordan Smith";

    private static TodoItem Todo(string id, string person = Assignee, TodoStatus status = TodoStatus.Pending) => new()
    {
        Id = id,
        Title = $"Task {id}",
        Category = "Work",
        ResponsiblePerson = person,
        Status = status,
        DueDate = DateTime.Now.Date.AddDays(3),
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(id[0]),
    };

    /// <summary>Four pending todos assigned to someone, and three unassigned ones.</summary>
    private static readonly ImmutableList<TodoItem> Initial =
    [
        Todo("a"), Todo("b"), Todo("c"), Todo("d"),
        Todo("u", "Unassigned"), Todo("v", "Unassigned"), Todo("w", "Unassigned"),
    ];

    private static readonly string[] Areas =
    [
        nameof(TodoLayoutAreas.AllItems), nameof(TodoLayoutAreas.TodosByCategory), nameof(TodoLayoutAreas.Summary),
        nameof(TodoLayoutAreas.Planning), nameof(TodoLayoutAreas.MyTasks), nameof(TodoLayoutAreas.Backlog),
        nameof(TodoLayoutAreas.TodaysFocus),
    ];

    /// <summary>What each area returned on its first render, captured as the area returned it.</summary>
    private readonly ReplaySubject<(string Area, UiControl Control)> rendered = new();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithPostingIdentity(PostingIdentity.System)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .WithTypes(typeof(TodoStatus), typeof(TodoEntry))
            .AddData(data => data.AddSource(ds => ds.WithType<TodoItem>(t => t
                .WithKey(todo => todo.Id)
                .WithInitialData(Initial))))
            .AddLayout(layout => Areas
                .Aggregate(layout, (l, area) => l.WithView(area, (host, ctx) => Capture(area, Render(area, host, ctx))))
                // The template with a feed that NEVER answers: the page must still render.
                .WithView(NoDataArea, (host, _) => TodoLayoutAreas.Page(host, "Todo", Typography.H4, withAdd: true,
                    Observable.Never<IEnumerable<TodoEntry>>())));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private static UiControl Render(string area, LayoutAreaHost host, RenderingContext ctx) => area switch
    {
        nameof(TodoLayoutAreas.AllItems) => TodoLayoutAreas.AllItems(host, ctx),
        nameof(TodoLayoutAreas.TodosByCategory) => TodoLayoutAreas.TodosByCategory(host, ctx),
        nameof(TodoLayoutAreas.Summary) => TodoLayoutAreas.Summary(host, ctx),
        nameof(TodoLayoutAreas.Planning) => TodoLayoutAreas.Planning(host, ctx),
        nameof(TodoLayoutAreas.MyTasks) => TodoLayoutAreas.MyTasks(host, ctx),
        nameof(TodoLayoutAreas.Backlog) => TodoLayoutAreas.Backlog(host, ctx),
        _ => TodoLayoutAreas.TodaysFocus(host, ctx),
    };

    private UiControl Capture(string area, UiControl control)
    {
        rendered.OnNext((area, control));
        return control;
    }

    // ── Templates first ────────────────────────────────────────────────────────────────────────

    /// <summary>Every Todo area returns a control tree with no deferred view: nothing in it renders
    /// only once the todos have arrived; the rows are ONE bound list.</summary>
    [HubFact]
    public async Task EveryTodoArea_IsATemplate()
    {
        foreach (var area in Areas)
        {
            var stream = Open(area);
            await stream.GetControlStream(area).Should().Within(10.Seconds()).Match(
                c => c is LayoutGridControl, $"{area} renders its page", TestContext.Current.CancellationToken);
            var (_, control) = await rendered.Where(r => r.Area == area).Should().Within(10.Seconds())
                .Emit($"{area} was rendered", TestContext.Current.CancellationToken);

            EveryViewIsStatic(control);
            Descendants(control).OfType<ItemTemplateControl>().Should().ContainSingle(
                $"{area}'s rows are ONE bound list, not a control per todo");
        }
    }

    /// <summary>The page renders — title, Add action, and the row template — while its feed has not
    /// answered at all: its first emission waits on no data.</summary>
    [HubFact]
    public async Task ThePageRenders_BeforeAnyTodoHasArrived()
    {
        var stream = Open(NoDataArea);
        var list = await ListArea(stream, NoDataArea);
        await stream.GetControlStream($"{list}/{ItemTemplateControl.ViewArea}").Should().Within(10.Seconds()).Match(
            c => c is LayoutGridControl,
            "the row template is part of the page before any row exists",
            TestContext.Current.CancellationToken);
    }

    // ── Row-scoped actions ─────────────────────────────────────────────────────────────────────

    /// <summary>The primary action ("Start") in row k starts the todo of row k — for every k, each
    /// click with the row as it was first rendered — and touches no other todo.</summary>
    [HubFact]
    public async Task ThePrimaryActionInRowK_StartsTodoK()
    {
        var area = nameof(TodoLayoutAreas.AllItems);
        var stream = Open(area);
        var template = await Template(stream, area);
        var primary = await Child(stream, await Child(stream, await Child(stream, template, 3), 0), 0);
        var rows = await ItemRows(stream, area, r => r.Item?.ResponsiblePerson == Assignee, 4);

        for (var k = 0; k < rows.Length; k++)
        {
            Click(stream, primary, rows[k].Row);
            var started = rows.Take(k + 1).Select(r => TodoOf(r.Entry).Id).ToImmutableHashSet();
            await Todos().Should().Within(10.Seconds()).Match(
                todos => todos.Where(t => t.ResponsiblePerson == Assignee)
                    .All(t => (t.Status == TodoStatus.InProgress) == started.Contains(t.Id)),
                $"Start in row {k} starts todo {TodoOf(rows[k].Entry).Id} and no other",
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The list changes between the render and the click — the todo above is deleted, so
    /// another todo sits in the clicked slot — and Delete still deletes the todo that was clicked.</summary>
    [HubFact]
    public async Task AfterTheListChanged_DeleteStillActsOnTheClickedTodo()
    {
        var area = nameof(TodoLayoutAreas.AllItems);
        var stream = Open(area);
        var template = await Template(stream, area);
        var primary = await Child(stream, await Child(stream, await Child(stream, template, 3), 0), 0);
        var delete = await Child(stream, primary, 4);
        var rows = await ItemRows(stream, area, r => r.Item?.ResponsiblePerson == Assignee, 4);

        Click(stream, delete, rows[1].Row); // "b"
        await Todos().Should().Within(10.Seconds()).Match(t => t.All(x => x.Id != "b"),
            "Delete in row 1 deletes b", TestContext.Current.CancellationToken);
        await ItemRows(stream, area, r => r.Item?.ResponsiblePerson == Assignee, 3);

        Click(stream, delete, rows[2].Row); // "c" as first rendered; the slot now holds "d"
        await Todos().Should().Within(10.Seconds()).Match(
            t => t.All(x => x.Id != "c") && t.Any(x => x.Id == "d") && t.Any(x => x.Id == "a"),
            "the person clicked c; d, which moved into its slot, is not theirs to delete",
            TestContext.Current.CancellationToken);
    }

    /// <summary>In the Backlog, team member j's entry in row k's assignment menu assigns todo k to
    /// member j — for every k.</summary>
    [HubFact]
    public async Task TheAssignMenuInRowK_AssignsTodoK()
    {
        var area = nameof(TodoLayoutAreas.Backlog);
        var stream = Open(area);
        var template = await Template(stream, area);
        var assignMenu = await Child(stream, await Child(stream, await Child(stream, template, 3), 1), 0);
        var rows = await ItemRows(stream, area, r => r.Item is not null, 3);

        for (var k = 0; k < rows.Length; k++)
        {
            var member = ResponsiblePersons.AvailablePersons[k + 1];
            Click(stream, await Child(stream, assignMenu, k + 1), rows[k].Row);
            var id = TodoOf(rows[k].Entry).Id;
            await Todos().Should().Within(10.Seconds()).Match(
                todos => todos.Single(t => t.Id == id).ResponsiblePerson == member,
                $"member {k + 1} in row {k}'s menu assigns {id} to {member}",
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A section's group action acts on the group of THAT heading row: "Start All" on the
    /// pending section starts exactly the four assigned todos and none of the unassigned ones.</summary>
    [HubFact]
    public async Task TheGroupActionOfAHeadingRow_ActsOnItsGroup()
    {
        var area = nameof(TodoLayoutAreas.AllItems);
        var stream = Open(area);
        var template = await Template(stream, area);
        var group = await Child(stream, template, 1);
        var heading = (await Entries(stream, area, 1)).Single(r => r.Entry.Key == $"assigned-{TodoStatus.Pending}");

        Click(stream, group, heading.Row);
        await Todos().Should().Within(10.Seconds()).Match(
            todos => todos.All(t => (t.Status == TodoStatus.InProgress) == (t.ResponsiblePerson == Assignee)),
            "Start All on the assigned-pending section starts its four todos and nothing else",
            TestContext.Current.CancellationToken);
    }

    /// <summary>NEGATIVE CONTROL: the same buttons clicked with no row act on nothing — the todo comes
    /// from the row on the click and from nothing else.</summary>
    [HubFact]
    public async Task AClickWithNoRow_ActsOnNothing()
    {
        var area = nameof(TodoLayoutAreas.AllItems);
        var stream = Open(area);
        var template = await Template(stream, area);
        var primary = await Child(stream, await Child(stream, await Child(stream, template, 3), 0), 0);
        var group = await Child(stream, template, 1);
        await ItemRows(stream, area, r => r.Item is not null, Initial.Count);

        Click(stream, primary, row: null);
        Click(stream, group, row: null);

        await Todos().Where(todos => todos.Any(t => t.Status != TodoStatus.Pending)).Should().NotEmit(
            500.Milliseconds(), "with no row the actions cannot know which todo, and must not guess",
            TestContext.Current.CancellationToken);
    }

    // ── Projection ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The projection decides which secondary actions a todo offers, by its status.</summary>
    [Theory]
    [InlineData(TodoStatus.Pending, true, false, true)]
    [InlineData(TodoStatus.InProgress, false, true, true)]
    [InlineData(TodoStatus.Completed, false, false, false)]
    [InlineData(TodoStatus.Cancelled, false, false, false)]
    public void TheRowOffersTheActionsItsStatusAllows(TodoStatus status, bool complete, bool pause, bool cancel)
    {
        var row = TodoProjections.ItemRow(Todo("x", status: status), English, TodoStyles.Card);
        (row.CompleteStyle != TodoStyles.Hidden).Should().Be(complete);
        (row.PauseStyle != TodoStyles.Hidden).Should().Be(pause);
        (row.CancelStyle != TodoStyles.Hidden).Should().Be(cancel);
        row.ActionsStyle.Should().NotBe(TodoStyles.Hidden);
        row.AssignStyle.Should().Be(TodoStyles.Hidden);
        row.HeadingStyle.Should().Be(TodoStyles.Hidden, "an item row shows no heading");
    }

    /// <summary>
    /// The planning area hands out OPEN work only. A member's "Assign" takes the first entry of its
    /// group and Auto-Assign takes all of them, so a completed or cancelled unassigned todo in either
    /// group would be assigned to someone; and the backlog title must count the rows under it.
    /// </summary>
    [Fact]
    public void Planning_HandsOutOnlyOpenUnassignedWork_AndCountsWhatItLists()
    {
        var todos = ImmutableList.Create(
            Todo("a"),
            Todo("p", "Unassigned", TodoStatus.Completed),
            Todo("q", "Unassigned", TodoStatus.Cancelled),
            Todo("r", "Unassigned"),
            Todo("s", "Unassigned", TodoStatus.InProgress));

        var rows = TodoProjections.Planning(todos, English).ToList();

        var member = rows.Single(r => r.Key == $"member-{Assignee}");
        member.GroupAction.Should().Be(TodoGroupActions.AssignFirst);
        member.Group.Select(t => t.Id).Should().Equal(["r", "s"],
            "Assign takes the first of these: the finished 'p' and the cancelled 'q' must not be offered");

        var backlog = rows.Single(r => r.Key == "backlog");
        backlog.GroupAction.Should().Be(TodoGroupActions.AutoAssign);
        backlog.Group.Select(t => t.Id).Should().Equal(["r", "s"],
            "Auto-Assign hands out open work only");
        var listed = rows.Where(r => r.Item is not null).Select(r => TodoOf(r).Id).ToList();
        listed.Should().Equal(["r", "s"], "the backlog lists the open unassigned todos, soonest due first");
        backlog.Heading.Should().Be($"##### {English.T("todo.planning.unassigned", listed.Count)}",
            "the title counts the rows shown under it, not every unassigned todo");
    }

    /// <summary>With nothing open left to hand out, a member row offers no "Assign" and the planning
    /// area shows no backlog — finished and cancelled todos are not a backlog.</summary>
    [Fact]
    public void Planning_WithNoOpenUnassignedWork_OffersNoAssignment()
    {
        var todos = ImmutableList.Create(
            Todo("a"),
            Todo("p", "Unassigned", TodoStatus.Completed),
            Todo("q", "Unassigned", TodoStatus.Cancelled));

        var rows = TodoProjections.Planning(todos, English).ToList();

        var member = rows.Single(r => r.Key == $"member-{Assignee}");
        member.GroupAction.Should().BeEmpty("there is nothing open to assign");
        member.GroupStyle.Should().Be(TodoStyles.Hidden);
        member.Group.Should().BeEmpty();
        rows.Should().NotContain(r => r.Key == "backlog");
        rows.Should().NotContain(r => r.Item != null);
    }

    /// <summary>One definition of "open" for the planning, backlog and focus areas: a cancelled todo
    /// is finished work in all three.</summary>
    [Theory]
    [InlineData(TodoStatus.Pending, true)]
    [InlineData(TodoStatus.InProgress, true)]
    [InlineData(TodoStatus.Completed, false)]
    [InlineData(TodoStatus.Cancelled, false)]
    public void OpenMeansNeitherCompletedNorCancelled_InEveryArea(TodoStatus status, bool open)
    {
        var todo = Todo("x", "Unassigned", status);
        ImmutableList<TodoItem> todos = [Todo("a"), todo];

        TodoProjections.IsOpen(todo).Should().Be(open);
        TodoProjections.Planning(todos, English).Any(r => r.Item?.Id == "x").Should().Be(open);
        TodoProjections.Backlog(todos, English).Any(r => r.Item?.Id == "x").Should().Be(open);
        TodoProjections.TodaysFocus([todo with { DueDate = DateTime.Now.Date }], English)
            .Any(r => r.Item?.Id == "x").Should().Be(open);
    }

    private static readonly TodoViewText English =
        new((key, args) => LocalizationCatalog.Get(key, "en", args), System.Globalization.CultureInfo.InvariantCulture);

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private ISynchronizationStream<JsonElement> Open(string area)
        => GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));

    /// <summary>The value, asserted present — the test fails here, by name, when it is not.</summary>
    private static T Present<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    /// <summary>The todo a row carries; a heading row carries none, and a test that asks for it there fails.</summary>
    private static TodoItem TodoOf(TodoEntry entry) => Present(entry.Item);

    private IObservable<IReadOnlyCollection<TodoItem>> Todos()
        => Present(GetHost().GetWorkspace().GetStream<TodoItem>()).Select(t => (IReadOnlyCollection<TodoItem>)(t ?? []));

    /// <summary>The area of the page's row list (its last child).</summary>
    private static async Task<string> ListArea(ISynchronizationStream<JsonElement> stream, string area)
    {
        var page = Assert.IsType<LayoutGridControl>(await stream.GetControlStream(area).Should().Within(10.Seconds()).Match(
            c => c is LayoutGridControl, "the page renders", TestContext.Current.CancellationToken));
        return Present(page.Areas.Last().Area.ToString());
    }

    private static async Task<string> Template(ISynchronizationStream<JsonElement> stream, string area)
    {
        var template = $"{await ListArea(stream, area)}/{ItemTemplateControl.ViewArea}";
        await stream.GetControlStream(template).Should().Within(10.Seconds()).Match(
            c => c is LayoutGridControl, "the row template renders once", TestContext.Current.CancellationToken);
        return template;
    }

    private static async Task<string> Child(ISynchronizationStream<JsonElement> stream, string area, int index)
    {
        var container = Assert.IsAssignableFrom<IContainerControl>(await stream.GetControlStream(area).Should().Within(10.Seconds()).Match(
            c => c is IContainerControl, $"{area} renders", TestContext.Current.CancellationToken));
        return Present(container.Areas.ElementAt(index).Area.ToString());
    }

    /// <summary>The rows as the client's mirror holds them, once it holds at least
    /// <paramref name="min"/>, each with the <see cref="RowContext"/> the client posts for it.</summary>
    private async Task<ImmutableArray<(TodoEntry Entry, RowContext Row)>> Entries(
        ISynchronizationStream<JsonElement> stream, string area, int min)
    {
        var pointer = LayoutAreaReference.GetDataPointer(TodoLayoutAreas.EntriesId);
        var rows = await stream.GetDataStream<JsonElement>(new JsonPointerReference(pointer))
            .Should().Within(10.Seconds()).Match(
                r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= min,
                $"the client mirror holds {area}'s rows", TestContext.Current.CancellationToken);
        return [.. rows.EnumerateArray().Select((r, i) => (
            Present(r.Deserialize<TodoEntry>(GetClient().JsonSerializerOptions)),
            new RowContext { Pointer = $"{pointer}/{i}", Index = i, Value = r.Clone() }))];
    }

    private async Task<ImmutableArray<(TodoEntry Entry, RowContext Row)>> ItemRows(
        ISynchronizationStream<JsonElement> stream, string area, Func<TodoEntry, bool> which, int count)
    {
        var pointer = LayoutAreaReference.GetDataPointer(TodoLayoutAreas.EntriesId);
        await stream.GetDataStream<JsonElement>(new JsonPointerReference(pointer))
            .Should().Within(10.Seconds()).Match(
                r => r.ValueKind == JsonValueKind.Array && r.EnumerateArray()
                    .Count(e => e.Deserialize<TodoEntry>(GetClient().JsonSerializerOptions) is { } entry && which(entry)) == count,
                $"{area} shows {count} matching todo rows", TestContext.Current.CancellationToken);
        return [.. (await Entries(stream, area, count)).Where(r => which(r.Entry))];
    }

    private static void Click(ISynchronizationStream<JsonElement> stream, string area, RowContext? row)
        => stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId) { Row = row },
            actingUser: null, onRefused: null, onAccepted: null);

    private static void EveryViewIsStatic(UiControl root)
    {
        foreach (var control in Descendants(root))
        {
            if (Member(control.GetType(), "Views")?.GetValue(control) is not IEnumerable views)
                continue;
            var deferred = views.OfType<object>().Where(v => v is not UiControl).Select(v => v.GetType().Name).ToList();
            deferred.Should().BeEmpty(
                $"{control.GetType().Name} carries {deferred.Count} deferred view(s) — a view that renders only once "
                + "data has arrived. A template binds its data instead.");
        }
    }

    private static IEnumerable<UiControl> Descendants(UiControl root)
    {
        yield return root;
        if (root is ItemTemplateControl template)
            foreach (var d in Descendants(template.View))
                yield return d;
        if (Member(root.GetType(), "Views")?.GetValue(root) is IEnumerable views)
            foreach (var v in views)
                if (v is UiControl child)
                    foreach (var d in Descendants(child))
                        yield return d;
    }

    private static PropertyInfo? Member(Type? t, string name)
    {
        for (; t is not null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (p is not null)
                return p;
        }
        return null;
    }
}
