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
/// The PATH-ADDRESSED branch of the <c>$Data</c> area (<see cref="DataPathViews"/>):
/// <c>$Data/Orders/10248</c>. <see cref="DataReferenceAreaIsATemplateTest"/> pins the self
/// reference only; this pins what the non-empty path adds.
///
/// <list type="bullet">
/// <item>The two data slots are named after the path with its <c>/</c> replaced
/// (<c>dataView_Orders_10248</c>, <c>showFull_Orders_10248</c>). A slot id is one segment of a
/// <c>/data/{id}</c> pointer, so a slash left in it would address a nested member nobody
/// writes.</item>
/// <item>The template arrives while the referenced data is silent, the data then reaches the
/// SAME slot, and a later change follows it with the control tree untouched.</item>
/// <item>A path the workspace cannot open is REPORTED in the slot
/// (<c>data.streamUnavailable</c>), never thrown and never left on the loading line.</item>
/// </list>
///
/// <para>Negative controls, run before this file was committed: with the <c>Replace("/", "_")</c>
/// removed from <c>DataContentView</c> the first test fails; with the mapped-collection guard
/// removed from <c>OpenData</c> the unopenable path throws <c>ArgumentException: Collections
/// NoSuchCollection are not mapped to any source</c> out of the render and the second test
/// fails — which is what the area did before this test existed.</para>
/// </summary>
public class DataReferenceAreaPathBranchTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string OrderPath = "Orders/10248";
    private const string UnopenablePath = "NoSuchCollection/1";

    // Per test-instance: the order behind the virtual path, held silent until the test releases it.
    private readonly Subject<object?> order = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddData(data => data
                .AddSource(ds => ds.WithType<TestTaskItem>(t => t.WithInitialData(TestTaskItem.InitialData)))
                .WithVirtualPath("Orders", (_, id) => id == "10248" ? order : Observable.Never<object?>()))
            .AddLayout(layout => layout);

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task APathAddressedArea_RendersBeforeItsData_InSlotsNamedAfterTheSanitizedPath()
    {
        var reference = new LayoutAreaReference(DataPathViews.DataAreaName) { Id = OrderPath };
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        // 1. The template arrives while the order has not emitted anything.
        var root = await stream.GetControlStream(DataPathViews.DataAreaName)
            .Should().Within(10.Seconds()).Match(c => c is StackControl,
                "the $Data area is a template for a path too: its controls do not wait for the data");
        var areas = root.Should().BeOfType<StackControl>().Subject.Areas;
        areas.Should().HaveCount(2, "a markdown block and the 'Load all' button");

        var markdown = await stream.GetControlStream(areas[0].Area.ToString() ?? "")
            .Should().Within(10.Seconds()).Match(c => c is MarkdownControl);
        var block = markdown.Should().BeOfType<MarkdownControl>().Subject;
        block.Markdown.Should().BeOfType<JsonPointerReference>(
            "the block's text is a POINTER into /data, never the JSON baked in on the hub");
        block.DataContext.Should().Be(LayoutAreaReference.GetDataPointer("dataView_Orders_10248"),
            "the slot is named after the path with its slash replaced — one pointer segment");

        // 2. The sanitized slot shows the loading line while the order is silent.
        var view = stream.GetDataStream<JsonElement>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer("dataView_Orders_10248")));
        await view.Where(v => Markdown(v) == "_Loading…_")
            .Should().Within(10.Seconds()).Emit("the bound slot shows the loading shape until the data answers");

        // 3. The order arrives: the SAME slot carries it.
        order.OnNext(new Order(10248, "Vins et alcools Chevalier"));
        await view.Where(v => Markdown(v)?.Contains("Vins et alcools Chevalier") == true)
            .Should().Within(10.Seconds()).Emit("the projection of the referenced data reaches the bound slot");

        // 4. A later change follows it — the binding stays live, the tree is not rebuilt.
        order.OnNext(new Order(10248, "Toms Spezialitäten"));
        await view.Where(v => Markdown(v)?.Contains("Toms Spezialit") == true)
            .Should().Within(10.Seconds()).Emit("a later change of the data reaches the same slot");

        var current = await stream.GetControlStream(DataPathViews.DataAreaName)
            .Should().Within(10.Seconds()).Emit();
        current.Should().BeOfType<StackControl>()
            .Which.Areas.Select(a => a.Area.ToString()).Should().Equal(areas.Select(a => a.Area.ToString()),
                "the data flowed through the binding — the control tree was not rebuilt");
    }

    [HubFact]
    public async Task APathTheWorkspaceCannotOpen_IsReportedInTheSlot()
    {
        var reference = new LayoutAreaReference(DataPathViews.DataAreaName) { Id = UnopenablePath };
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        await stream.GetControlStream(DataPathViews.DataAreaName)
            .Should().Within(10.Seconds()).Match(c => c is StackControl,
                "an unopenable path still gets its template — the failure is data, not an absent page");

        var view = stream.GetDataStream<JsonElement>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer("dataView_NoSuchCollection_1")));
        var reported = await view.Where(v => Markdown(v) is { } text && text != "_Loading…_")
            .Should().Within(10.Seconds()).Emit("the slot must leave the loading line and say what happened");
        Markdown(reported).Should().Be($"_Unable to get a data stream for `{UnopenablePath}`._",
            "a path the workspace cannot open is named in the slot (data.streamUnavailable)");
    }

    /// <summary>
    /// The control for the refusal above: a path whose first segment IS a mapped collection is
    /// opened, so the guard that reports an unopenable path does not swallow a real one.
    /// </summary>
    [HubFact]
    public async Task AMappedCollectionPath_IsOpened_NotReportedUnavailable()
    {
        var reference = new LayoutAreaReference(DataPathViews.DataAreaName) { Id = "TestTaskItem/task-1" };
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var view = stream.GetDataStream<JsonElement>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer("dataView_TestTaskItem_task-1")));
        await view.Where(v => Markdown(v)?.Contains("First Task") == true)
            .Should().Within(10.Seconds()).Emit("an entity of a mapped collection is read through its path");
    }

    private sealed record Order(int OrderId, string ShipName);

    private static string? Markdown(JsonElement view)
        => view.ValueKind == JsonValueKind.Object
           && view.TryGetProperty("markdown", out var m)
           && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
}
