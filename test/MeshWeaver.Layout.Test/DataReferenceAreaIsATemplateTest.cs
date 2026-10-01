using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The <c>$Data</c> area (<see cref="DataPathViews"/>) is a TEMPLATE (Doc/GUI/DataBinding →
/// "Templates first, data later").
///
/// <para><b>What it was.</b> <c>DataContentView</c> returned
/// <c>data.CombineLatest(showFull, (d, s) =&gt; Controls.Markdown(json…))</c>: the area emitted
/// NOTHING until the referenced data answered, and then emitted a fresh control per data change
/// with the JSON baked into it.</para>
///
/// <para><b>What this pins.</b> The data source here never emits until the test says so. The
/// area's control must arrive anyway — a stack of a markdown block and a "Load all" button whose
/// values are POINTERS into <c>/data</c> — with a loading line in the bound slot; then the JSON
/// reaches the SAME slot, and a later change of the data follows it, the control untouched.</para>
///
/// <para>Negative control, run before this file was committed: against the pre-conversion area
/// the first wait times out — the control stream for <c>$Data</c> stays empty while the source
/// is silent.</para>
/// </summary>
public class DataReferenceAreaIsATemplateTest(ITestOutputHelper output) : HubTestBase(output)
{
    // Per test-instance (the fixture builds one host per test): the default data reference, held
    // silent until the test releases it.
    private readonly Subject<TestTaskItem?> source = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddData(data => data
                .AddSource(ds => ds.WithType<TestTaskItem>(t => t.WithInitialData(TestTaskItem.InitialData)))
                .WithDefaultDataReference(_ => source))
            .AddLayout(layout => layout);

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task TheDataArea_RendersBeforeItsData_AndBindsIt()
    {
        var reference = new LayoutAreaReference(DataPathViews.DataAreaName);
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        // 1. The template arrives while the data source has not emitted anything.
        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c is StackControl,
                "the $Data area is a template: its controls do not wait for the data");
        var areas = ((StackControl)root!).Areas;
        areas.Should().HaveCount(2, "a markdown block and the 'Load all' button");

        var markdown = await stream.GetControlStream(areas[0].Area.ToString()!)
            .Should().Within(10.Seconds()).Match(c => c is MarkdownControl);
        var slot = ((MarkdownControl)markdown!).Markdown.Should().BeOfType<JsonPointerReference>(
            "the block's text is a POINTER into /data, never the JSON baked in on the hub").Subject;
        var button = await stream.GetControlStream(areas[1].Area.ToString()!)
            .Should().Within(10.Seconds()).Match(c => c is ButtonControl);
        ((ButtonControl)button!).Style.Should().BeOfType<JsonPointerReference>(
            "the button's visibility is bound — it appears only when the projection truncates");

        var view = stream.GetDataStream<JsonElement>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer("dataView_self")));
        await view.Where(v => Markdown(v) == "_Loading…_")
            .Should().Within(10.Seconds()).Emit("the bound slot shows the loading shape until the data answers");
        slot.Pointer.Should().Be("markdown");

        // 2. The data arrives: the SAME slot carries it.
        source.OnNext(TestTaskItem.InitialData[0]);
        await view.Should().Within(10.Seconds()).Match(v => Markdown(v)?.Contains("First Task") == true,
            "the projection of the data reaches the bound slot");

        // 3. A later change follows it — the binding stays live.
        source.OnNext(TestTaskItem.InitialData[0] with { Title = "Renamed Task" });
        await view.Where(v => Markdown(v)?.Contains("Renamed Task") == true)
            .Should().Within(10.Seconds()).Emit("a later change of the data reaches the same slot");

        var current = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Emit();
        current.Should().BeOfType<StackControl>()
            .Which.Areas.Select(a => a.Area.ToString()).Should().Equal(areas.Select(a => a.Area.ToString()),
                "the data flowed through the binding — the control tree was not rebuilt");
    }

    private static string? Markdown(JsonElement view)
        => view.ValueKind == JsonValueKind.Object
           && view.TryGetProperty("markdown", out var m)
           && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
}
