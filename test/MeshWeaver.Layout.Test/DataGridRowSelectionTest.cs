using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins first-class grid row selection (<c>Doc/GUI/DataGrid → "Row selection"</c>): the rules every client view
/// applies (select-all selects every SELECTABLE row, the header clears a non-empty selection, a
/// non-selectable row carries its reason and can never be ticked), the owner-side re-check, and the
/// round trip — the client writes the selected keys into the data section, the bulk button's click
/// handler reads them, and the bulk click gets the same busy state as any click.
/// </summary>
public class DataGridRowSelectionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "Inbox";
    private const string GridArea = Area + "/Grid";
    private const string BulkArea = Area + "/Bulk";
    private const string SelectionId = "inboxSelection";

    private readonly ReplaySubject<ImmutableList<string>> actedOn = new(1);

    private sealed record Row(string Path, string Title, string? Blocked);

    private static readonly Row[] Rows =
    [
        new("a/1", "First", null),
        new("a/2", "Second", "You requested this yourself"),
        new("a/3", "Third", null),
    ];

    private static JsonObject[] JsonRows => Rows.Select(r => new JsonObject
    {
        ["path"] = r.Path, ["title"] = r.Title, ["blocked"] = r.Blocked,
    }).ToArray();

    [Fact]
    public void SelectAllSelectsEverySelectableRowAndNoOther()
        => DataGridRowSelection.SelectAll(JsonRows, "path", "blocked").Should().Equal("a/1", "a/3");

    [Fact]
    public void TheHeaderTogglesBetweenAllSelectableAndNone()
    {
        ImmutableList<string> none = [];
        DataGridRowSelection.HeaderState(JsonRows, none, "path", "blocked").Should().Be(DataGridHeaderSelection.None);
        var all = DataGridRowSelection.ToggleAll(JsonRows, none, "path", "blocked");
        all.Should().Equal("a/1", "a/3");
        DataGridRowSelection.HeaderState(JsonRows, all, "path", "blocked").Should().Be(DataGridHeaderSelection.All,
            "every SELECTABLE row is selected — the blocked one does not count against 'all'");
        DataGridRowSelection.ToggleAll(JsonRows, all, "path", "blocked").Should().BeEmpty("clicking a full header clears");
        DataGridRowSelection.HeaderState(JsonRows, ["a/1"], "path", "blocked").Should().Be(DataGridHeaderSelection.Some);
        DataGridRowSelection.ToggleAll(JsonRows, ["a/1"], "path", "blocked").Should().BeEmpty("an indeterminate header clears");
    }

    [Fact]
    public void ANonSelectableRowCarriesItsReasonAndCannotBeTicked()
    {
        var blocked = JsonRows[1];
        DataGridRowSelection.DisabledReasonOf(blocked, "blocked").Should().Be("You requested this yourself");
        DataGridRowSelection.Toggle([], blocked, on: true, "path", "blocked").Should().BeEmpty();
        // Control: a selectable row is ticked by the same call.
        DataGridRowSelection.Toggle([], JsonRows[0], on: true, "path", "blocked").Should().Equal("a/1");
    }

    [Fact]
    public void TheOwnerReChecksTheSelectionBeforeActing()
        => DataGridRowSelection.Prune(Rows, ["a/2", "a/3", "gone"], r => r.Path, r => r.Blocked)
            .Select(r => r.Path).Should().Equal(["a/3"], "a blocked row and a vanished row are never acted on");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(Area, (host, _) =>
            {
                host.SeedRowSelection(SelectionId);
                return Controls.Stack
                    .WithView(new DataGridControl(Rows)
                        .WithColumn(new PropertyColumnControl<string> { Property = "title" })
                        .WithRowSelection(SelectionId, "path", "blocked"), "Grid")
                    .WithView(Controls.Button("Approve selected").WithReactiveClickAction(ctx =>
                        ctx.SelectedRowKeys(SelectionId)
                            .Select(keys => DataGridRowSelection.Prune(Rows, keys, r => r.Path, r => r.Blocked))
                            .Do(rows =>
                            {
                                ctx.ReportSummary($"Approved {rows.Count} of {rows.Count}");
                                actedOn.OnNext(rows.Select(r => r.Path).ToImmutableList());
                            })
                            .Select(_ => Unit.Default)), "Bulk");
            }));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [HubFact]
    public async Task TheBulkActionReadsTheSelectionTheClientWrote()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        var grid = (DataGridControl)(await stream.GetControlStream(GridArea).Should().Within(10.Seconds())
            .Match(c => c is DataGridControl, "the grid renders", ct))!;
        grid.SelectionMode.Should().Be(DataGridRowSelection.Multiple);
        grid.SelectionKey.Should().Be("path");
        grid.SelectionDisabledReason.Should().Be("blocked");
        var pointer = grid.SelectedItems.Should().BeOfType<JsonPointerReference>().Subject.Pointer;

        // What the client's header checkbox writes: select-all over the rows it renders — including the
        // blocked one sneaked in, as a stale client might.
        var keys = DataGridRowSelection.SelectAll(JsonRows, "path", "blocked").Add("a/2");
        stream.UpdatePointer(new JsonArray(keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
            pointer, new JsonPointerReference("keys"));
        await stream.GetDataStream<DataGridSelectionState>(new JsonPointerReference(pointer))
            .Should().Within(10.Seconds()).Match(s => s is { Keys.Count: 3 }, "the selection reached the owner's data section", ct);

        var answer = new ReplaySubject<string?>(1);
        stream.SubmitUserAction(new ClickedEvent(BulkArea, stream.StreamId), actingUser: null,
            onRefused: answer.OnNext, onAccepted: () => answer.OnNext(null));

        (await actedOn.Should().Within(10.Seconds()).Emit("the bulk action ran over the selection", ct))
            .Should().Equal(["a/1", "a/3"], "the owner's re-check drops the blocked row");
        (await answer.Should().Within(10.Seconds()).Emit("the bulk click is answered", ct)).Should().BeNull();
        await stream.GetDataStream<ClickProgress>(new JsonPointerReference(ClickProgress.PointerFor(BulkArea)))
            .Should().Within(10.Seconds()).Match(p => p is { Running: false, Summary: "Approved 2 of 2" },
                "the bulk button settles with its summary like any click", ct);
    }
}
