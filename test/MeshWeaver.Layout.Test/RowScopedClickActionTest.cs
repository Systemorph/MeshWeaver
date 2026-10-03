using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the owner half of ROW-SCOPED ACTIONS (Doc/GUI/DataBinding → "Row-scoped actions"): a button
/// declared inside a bound row template — a <c>BindMany</c> list or a data grid's template column — is
/// rendered by the client once per row, but exists on the owner ONCE, at the template's area. Before
/// this, its <see cref="ClickedEvent"/> named only that area, so every row's button ran the same
/// action with nothing saying which row, and the data-binding batches had to downgrade per-row
/// buttons to "click the row, then act". The event now carries the row as the client rendered it
/// (<see cref="ClickedEvent.Row"/>), and the action reads it from <see cref="UiActionContext.Row"/>.
///
/// <para>The client half — the Blazor views cascading the row and stamping it on the event — is
/// pinned in MeshWeaver.Plugins (<c>RowScopedActionsFromViewsTest</c>). This suite posts exactly
/// what that client posts, read off the client's own mirror of the layout stream.</para>
/// </summary>
public class RowScopedClickActionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ListArea = "RowList";
    private const string ListDataId = "fruits";
    private const string GridArea = "RowGrid";
    private const string BlurArea = "RowBlurList";
    private const string BlurDataId = "blurFruits";

    /// <summary>The row record the templates bind to. A node-like <see cref="Path"/> so the node-row
    /// accessor is exercised too.</summary>
    public record Fruit(string Id, string Label, string Path);

    private static Fruit F(string id) => new(id, $"Fruit {id}", $"Orchard/{id}");

    private static readonly ImmutableList<Fruit> Initial = [F("a"), F("b"), F("c"), F("d")];

    private readonly BehaviorSubject<IEnumerable<Fruit>> fruits = new(Initial);

    /// <summary>Every row-scoped click the owner ran, in order.</summary>
    private readonly Subject<UiActionContext> clicks = new();

    private Task Record(UiActionContext ctx)
    {
        clicks.OnNext(ctx);
        return Task.CompletedTask;
    }

    /// <summary>Every row-scoped blur the owner ran, in order.</summary>
    private readonly Subject<UiActionContext> blurs = new();

    private Task RecordBlur(UiActionContext ctx)
    {
        blurs.OnNext(ctx);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(Fruit))
            .AddLayout(layout => layout
                // A bound list: ONE template, one button per row on the client.
                .WithView(ListArea, fruits.BindMany(ListDataId,
                    fruit => Controls.Button(fruit.Label).WithClickAction(ctx => Record(ctx))))
                // A bound list of inputs: ONE template, one field per row, each with a blur action.
                .WithView(BlurArea, fruits.BindMany(BlurDataId,
                    fruit => new TextFieldControl(fruit.Label).WithBlurAction(ctx => RecordBlur(ctx))))
                // A data grid whose template column holds the per-row button.
                .WithView(GridArea, new DataGridControl(Initial)
                    .WithColumn(new PropertyColumnControl<string> { Property = "label" })
                    .WithColumn(new TemplateColumnControl(
                        Controls.Button("Open").WithClickAction(ctx => Record(ctx))))));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// N rows, one button each: clicking row k runs the action with row k — its value, its pointer
    /// and its node path — for every k. The rows are distinct, so an action handed the wrong row
    /// (an off-by-one, the first row, the last one) fails here.
    /// </summary>
    [HubFact]
    public async Task EachRowsButtonActsOnItsOwnRow()
    {
        var stream = await OpenList();
        var rendered = await RenderedRows(stream, Initial.Count);

        for (var k = 0; k < Initial.Count; k++)
        {
            var ctx = await Click(stream, ListTemplateArea, RowAsTheClientRendersIt(rendered, k));

            ctx.RowAs<Fruit>().Should().Be(Initial[k], $"the button in row {k} was clicked");
            ctx.RowPath().Should().Be(Initial[k].Path, "a node row names its node");
            Assert.NotNull(ctx.Row);
            ctx.Row.Pointer.Should().Be($"{ListPointer}/{k}");
            ctx.Row.Index.Should().Be(k);
        }
    }

    /// <summary>
    /// The BLUR twin of the test above. A form control declared inside a bound row template also
    /// exists ONCE on the owner, so its <see cref="BlurEvent"/> names only the template's area; the
    /// owner relays the event's <see cref="BlurEvent.Row"/> to the blur action exactly as it does for a
    /// click. Every row is blurred, in an order that is not the render order, and each handler must
    /// see its OWN row — a relay that dropped the row, or handed every blur the same one, fails here.
    /// </summary>
    [HubFact]
    public async Task EachRowsFieldBlursWithItsOwnRow()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(BlurArea));
        var templateArea = $"{BlurArea}/{ItemTemplateControl.ViewArea}";
        var pointer = LayoutAreaReference.GetDataPointer(BlurDataId);
        await stream.GetControlStream(templateArea).Should().Within(10.Seconds()).Match(
            control => control is TextFieldControl,
            "the row template's field is rendered once, at the template's area",
            TestContext.Current.CancellationToken);
        var rendered = await RenderedRows(stream, pointer, Initial.Count);

        foreach (var k in new[] { 2, 0, 3, 1 })
        {
            var row = new RowContext { Pointer = $"{pointer}/{k}", Index = k, Value = rendered[k].Clone() };
            var ctx = await Blur(stream, templateArea, row);

            Assert.NotNull(ctx.Row);
            ctx.Row.Pointer.Should().Be($"{pointer}/{k}", $"the field in row {k} lost focus");
            ctx.Row.Index.Should().Be(k);
            ctx.RowAs<Fruit>().Should().Be(Initial[k], $"the field in row {k} lost focus");
            ctx.RowPath().Should().Be(Initial[k].Path);
        }
    }

    /// <summary>
    /// NEGATIVE CONTROL — the shape before this mechanism: the same button, clicked with no row on the
    /// event, still runs its action, and the action CANNOT tell which row it came from. So what the
    /// test above asserts comes from the row on the event and from nothing else on the owner.
    /// </summary>
    [HubFact]
    public async Task AClickWithoutARowCannotTellWhichRowItCameFrom()
    {
        var stream = await OpenList();
        await RenderedRows(stream, Initial.Count);

        var ctx = await Click(stream, ListTemplateArea, row: null);

        ctx.Row.Should().BeNull("the template exists once on the owner; nothing there names a row");
        ctx.RowAs<Fruit>().Should().BeNull();
        ctx.RowPath().Should().BeNull();
    }

    /// <summary>
    /// 🚨 The list changes between the render and the click — a row inserted above, another removed —
    /// and the click still acts on the row the person clicked. The owner never re-resolves the row by
    /// its index: re-read now, index 2 holds a DIFFERENT fruit, and an action that trusted it would
    /// archive, revoke or delete the wrong row. A fresh click on the re-rendered list acts on what is
    /// now there.
    /// </summary>
    [HubFact]
    public async Task ARowClickedAfterTheListChangedStillActsOnTheRowThatWasClicked()
    {
        var stream = await OpenList();
        var before = await RenderedRows(stream, Initial.Count);
        var clickedRow = RowAsTheClientRendersIt(before, 2); // "c", as the person saw it

        // Insert two rows above and remove the first: index 2 now holds "b".
        ImmutableList<Fruit> changed = [F("y"), F("z"), F("b"), F("c"), F("d")];
        fruits.OnNext(changed);
        var after = await RenderedRows(stream, changed.Count);
        after[2].GetProperty("id").GetString().Should().Be("b",
            "premise: the slot the person clicked now holds another row");

        var stale = await Click(stream, ListTemplateArea, clickedRow);
        stale.RowAs<Fruit>().Should().Be(F("c"),
            "the action must act on the row that was clicked, not on whatever moved into its slot");
        stale.RowPath().Should().Be("Orchard/c");

        var fresh = await Click(stream, ListTemplateArea, RowAsTheClientRendersIt(after, 2));
        fresh.RowAs<Fruit>().Should().Be(F("b"), "a click on the re-rendered row acts on what it shows");
    }

    /// <summary>
    /// A data grid's template column: its template is rendered into its own sub-area, so the button's
    /// click action is found there (before, it existed only inline in the grid and a click inside a
    /// cell reached the GRID's area), and the row arrives as the grid client renders it — a JSON object
    /// with no pointer, since a grid sorts and pages its rows on the client.
    /// </summary>
    [HubFact]
    public async Task ATemplateColumnButtonActsOnItsGridRow()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(GridArea));
        var columnArea = $"{GridArea}/{DataGridControl.TemplateColumnArea(1)}";

        await stream.GetControlStream(columnArea).Should().Within(10.Seconds()).Match(
            control => control is ButtonControl,
            "the template column's button is rendered into its own area, where a click names it",
            TestContext.Current.CancellationToken);

        for (var k = 0; k < Initial.Count; k++)
        {
            var row = new RowContext
            {
                Value = Assert.IsType<JsonObject>(
                    JsonSerializer.SerializeToNode(Initial[k], GetClient().JsonSerializerOptions))
            };
            var ctx = await Click(stream, columnArea, row);
            ctx.RowAs<Fruit>().Should().Be(Initial[k], $"the Open button in grid row {k} was clicked");
            ctx.RowPath().Should().Be(Initial[k].Path);
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static string ListPointer => LayoutAreaReference.GetDataPointer(ListDataId);

    private static string ListTemplateArea => $"{ListArea}/{ItemTemplateControl.ViewArea}";

    /// <summary>What the client posts for row <paramref name="k"/>: the row's pointer and the value
    /// it rendered (the Blazor <c>ItemTemplate</c> cascades exactly this to the row's views).</summary>
    private static RowContext RowAsTheClientRendersIt(ImmutableArray<JsonElement> rows, int k) => new()
    {
        Pointer = $"{ListPointer}/{k}",
        Index = k,
        Value = rows[k].Clone(),
    };

    private async Task<ISynchronizationStream<JsonElement>> OpenList()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(ListArea));
        await stream.GetControlStream(ListTemplateArea).Should().Within(10.Seconds()).Match(
            control => control is ButtonControl,
            "the row template is rendered once, at the template's area",
            TestContext.Current.CancellationToken);
        return stream;
    }

    /// <summary>The rows as the client's mirror holds them, once it holds <paramref name="count"/>.</summary>
    private static Task<ImmutableArray<JsonElement>> RenderedRows(
        ISynchronizationStream<JsonElement> stream, int count)
        => RenderedRows(stream, ListPointer, count);

    /// <summary>The rows under <paramref name="pointer"/> as the client's mirror holds them, once it
    /// holds <paramref name="count"/>.</summary>
    private static async Task<ImmutableArray<JsonElement>> RenderedRows(
        ISynchronizationStream<JsonElement> stream, string pointer, int count)
    {
        var rows = await stream.GetDataStream<JsonElement>(new JsonPointerReference(pointer))
            .Should().Within(10.Seconds()).Match(
                rows => rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() == count,
                $"the client mirror holds the {count} rows it renders",
                TestContext.Current.CancellationToken);
        return [.. rows.EnumerateArray().Select(r => r.Clone())];
    }

    /// <summary>Submits a click the way a client does and returns the context the action ran with.</summary>
    private async Task<UiActionContext> Click(
        ISynchronizationStream<JsonElement> stream, string area, RowContext? row)
    {
        var ran = new ReplaySubject<UiActionContext>(1);
        using var _ = clicks.Subscribe(ran);
        stream.SubmitUserAction(
            new ClickedEvent(area, stream.StreamId) { Row = row },
            actingUser: null, onRefused: null, onAccepted: null);
        var ctx = await ran.Should().Within(10.Seconds()).Emit(
            "the clicked button's action runs on the owner", TestContext.Current.CancellationToken);
        Assert.NotNull(ctx);
        return ctx;
    }

    /// <summary>Submits a blur the way a client does and returns the context the action ran with.</summary>
    private async Task<UiActionContext> Blur(
        ISynchronizationStream<JsonElement> stream, string area, RowContext? row)
    {
        var ran = new ReplaySubject<UiActionContext>(1);
        using var _ = blurs.Subscribe(ran);
        stream.SubmitUserAction(
            new BlurEvent(area, stream.StreamId) { Row = row },
            actingUser: null, onRefused: null, onAccepted: null);
        var ctx = await ran.Should().Within(10.Seconds()).Emit(
            "the blurred field's action runs on the owner", TestContext.Current.CancellationToken);
        Assert.NotNull(ctx);
        return ctx;
    }
}
