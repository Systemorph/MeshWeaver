using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="DataGridBinding.BindGrid{TRow}"/> and <see cref="FeedBinding.BindMarkdown"/> — the
/// "template + feed" helpers the converted settings tabs use (Doc/GUI/DataBinding → "Templates
/// first, data later").
///
/// <para>Each feed here is a subject the test holds silent. The control must arrive anyway, bound
/// by pointer to <c>/data</c>, with the loading shape in its slot; the first value, a later value,
/// an empty value and a failure must each reach the SAME control's slot.</para>
///
/// <para>Negative control, run before this file was committed: with the views written the old way
/// — <c>rows.Select(r =&gt; grid)</c> / <c>markdown.Select(Controls.Markdown)</c> — the first wait
/// fails: nothing renders while the feed is silent.</para>
/// </summary>
public class FedControlsAreTemplatesTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string GridView = nameof(GridView);
    private const string MarkdownView = nameof(MarkdownView);

    // Per test instance (the fixture builds one host per test): the feeds, silent until released.
    private readonly Subject<IReadOnlyList<Row>> rows = new();
    private readonly Subject<string> markdown = new();

    /// <summary>A plain grid row.</summary>
    public record Row(string Name);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(GridView, (_, _) => rows
                    .BindGrid("rows", "nothing here", message => $"failed: {message}")
                    .WithColumn(new PropertyColumnControl<string> { Property = "name" }))
                .WithView(MarkdownView, (_, _) => markdown
                    .BindMarkdown("md", "loading…", ex => $"failed: {ex.Message}")));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private ISynchronizationStream<JsonElement> Render(string area)
        => GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));

    private static IObservable<JsonElement> Slot(ISynchronizationStream<JsonElement> stream, string id, params string[] path)
        => stream.GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id, path)));

    [HubFact]
    public async Task TheGrid_RendersBeforeItsRows_AndEveryRowSetReachesIt()
    {
        var stream = Render(GridView);

        var grid = await stream.GetControlStream(GridView)
            .Should().Within(10.Seconds()).Match(c => c is DataGridControl,
                "the grid is a template: it does not wait for its rows");
        ((DataGridControl)grid!).Data.Should().BeOfType<JsonPointerReference>(
            "the rows are a POINTER into /data, never a list baked in on the hub");
        ((DataGridControl)grid!).Loading.Should().BeOfType<JsonPointerReference>("the loading shape is bound");
        await Loading(stream).Where(l => l)
            .Should().Within(10.Seconds()).Emit("the grid shows its loading shape until the first row set");

        rows.OnNext([new Row("alpha"), new Row("beta")]);
        await Slot(stream, "rows").Where(v => Names(v).SequenceEqual(["alpha", "beta"]))
            .Should().Within(10.Seconds()).Emit("the first row set reaches the bound slot");
        await Loading(stream).Where(l => !l)
            .Should().Within(10.Seconds()).Emit("loading ends with the first row set — read the way the grid view reads it");

        rows.OnNext([]);
        await Slot(stream, "rows").Where(v => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0)
            .Should().Within(10.Seconds()).Emit("a later, empty row set reaches the same slot");
        await Empty(stream).Where(e => e == "nothing here")
            .Should().Within(10.Seconds()).Emit("the grid's bound empty text");

        rows.OnError(new InvalidOperationException("boom"));
        await Empty(stream).Where(e => e == "failed: boom")
            .Should().Within(10.Seconds()).Emit("a failed feed is reported in the grid, never swallowed");
    }

    [HubFact]
    public async Task TheMarkdown_RendersBeforeItsText_AndFollowsIt()
    {
        var stream = Render(MarkdownView);

        var control = await stream.GetControlStream(MarkdownView)
            .Should().Within(10.Seconds()).Match(c => c is MarkdownControl,
                "the markdown is a template: it does not wait for its text");
        ((MarkdownControl)control!).Markdown.Should().BeOfType<JsonPointerReference>();
        await Slot(stream, "md").Where(v => v.GetString() == "loading…")
            .Should().Within(10.Seconds()).Emit("the loading shape until the feed answers");

        markdown.OnNext("# first");
        await Slot(stream, "md").Where(v => v.GetString() == "# first")
            .Should().Within(10.Seconds()).Emit();
        markdown.OnNext("# second");
        await Slot(stream, "md").Where(v => v.GetString() == "# second")
            .Should().Within(10.Seconds()).Emit("a later value reaches the same slot");

        markdown.OnError(new InvalidOperationException("boom"));
        await Slot(stream, "md").Where(v => v.GetString() == "failed: boom")
            .Should().Within(10.Seconds()).Emit("a failed feed is reported, never swallowed");
    }

    // Read through DataBind — the path the Blazor grid view binds Loading / EmptyContent with.
    private static IObservable<bool> Loading(ISynchronizationStream<JsonElement> stream)
        => stream.DataBind<bool>(new JsonPointerReference(
            LayoutAreaReference.GetDataPointer(DataGridBinding.LoadingId("rows"))));

    private static IObservable<string> Empty(ISynchronizationStream<JsonElement> stream)
        => stream.DataBind<string>(new JsonPointerReference(
            LayoutAreaReference.GetDataPointer(DataGridBinding.EmptyId("rows"))));

    private static IEnumerable<string?> Names(JsonElement rowsElement)
        => rowsElement.ValueKind == JsonValueKind.Array
            ? rowsElement.EnumerateArray().Select(r => r.TryGetProperty("name", out var n) ? n.GetString() : null)
            : [];
}
