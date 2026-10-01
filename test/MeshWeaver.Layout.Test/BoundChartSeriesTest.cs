using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Json;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// A <see cref="ChartControl"/>'s <see cref="ChartControl.Series"/> and <see cref="ChartControl.Labels"/>
/// are BINDABLE already — both are <c>object?</c> slots, and both renderers resolve them through
/// the generic binding (Blazor <c>RadzenChartView</c> via <c>BlazorView.DataBind</c>, React
/// <c>chart.tsx</c> via <c>useResolve</c>). So a chart area is a TEMPLATE: it declares a chart
/// whose series point into <c>/data</c>, and the data that feeds them follows on its own
/// (Doc/GUI/DataBinding → "Binding a rich control to a node field").
///
/// <para>This pins the wire contract a renderer relies on: the control carries a POINTER (not the
/// series), the pointer resolves to typed <see cref="ChartSeries"/> through the same
/// <see cref="LayoutClientExtensions.DataBind{T}"/> the Blazor views use, and it FOLLOWS a change to
/// the fed data. Negative control: a pointer to a different data entry does not see that change.</para>
/// </summary>
public class BoundChartSeriesTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string BoundChart = nameof(BoundChart);
    private const string SeriesId = "chartSeries";
    private const string OtherSeriesId = "otherSeries";
    private const string LabelsId = "chartLabels";

    private static ImmutableList<ChartSeries> Series(params double[] values) =>
        ImmutableList.Create<ChartSeries>(new BarSeries(values, "Premium"));

    private static IObservable<UiControl?> BoundChartView(LayoutAreaHost host, RenderingContext _)
    {
        // The FEED: in a real area a stream (a node query, a computed projection) writes these;
        // the control below never holds the numbers.
        host.UpdateData(SeriesId, Series(1, 2));
        host.UpdateData(OtherSeriesId, Series(7, 8));
        host.UpdateData(LabelsId, ImmutableList.Create("2025", "2026"));
        return Observable.Return<UiControl?>(new ChartControl
        {
            Series = new JsonPointerReference(LayoutAreaReference.GetDataPointer(SeriesId)),
            Labels = new JsonPointerReference(LayoutAreaReference.GetDataPointer(LabelsId)),
        }.WithTitle("Economics"));
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout.WithView(BoundChart, BoundChartView));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private static double[] Values(ImmutableList<ChartSeries> series) =>
        series.Single().Data switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } je => je.EnumerateArray().Select(v => v.GetDouble()).ToArray(),
            System.Collections.IEnumerable items => items.Cast<object>()
                .Select(v => v is JsonElement e ? e.GetDouble() : Convert.ToDouble(v)).ToArray(),
            _ => [],
        };

    [HubFact]
    public async Task ABoundChart_RendersItsSeries_AndFollowsAChangeToTheFeed()
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(BoundChart));

        var chart = (ChartControl)(await stream.GetControlStream(BoundChart)
            .Should().Within(10.Seconds()).Match(c => c is not null))!;
        var seriesPointer = chart.Series.Should().BeOfType<JsonPointerReference>(
            "the area declares WHERE the series live, never the numbers").Subject;
        chart.Labels.Should().BeOfType<JsonPointerReference>();

        // Resolve the way RadzenChartView does: the bound JSON, deserialized with the hub's options.
        var options = client.JsonSerializerOptions;
        ImmutableList<ChartSeries>? ToSeries(object? raw, ImmutableList<ChartSeries>? _) =>
            raw is JsonElement je ? je.Deserialize<ImmutableList<ChartSeries>>(options) : raw as ImmutableList<ChartSeries>;

        var bound = stream.DataBind<ImmutableList<ChartSeries>>(seriesPointer, conversion: ToSeries).Replay();
        var other = stream.DataBind<ImmutableList<ChartSeries>>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer(OtherSeriesId)), conversion: ToSeries).Replay();
        using var c1 = bound.Connect();
        using var c2 = other.Connect();

        Values(await bound.Should().Within(10.Seconds()).Match(s => s.Count == 1, "the series render from the feed"))
            .Should().Equal(1, 2);
        Values(await other.Should().Within(10.Seconds()).Emit("the control entry renders too"))
            .Should().Equal(7, 8);

        // The feed changes (a new row arrived). Patch the data entry the pointer names.
        var replacement = JsonSerializer.SerializeToNode(Series(5, 6), options)!;
        stream.Update(ci =>
        {
            var patch = new JsonPatch(PatchOperation.Replace(JsonPointer.Parse(seriesPointer.Pointer), replacement));
            return stream.ToChangeItem(ci, patch.Apply(ci), patch, stream.StreamId);
        }, null!);

        Values(await bound.Should().Within(10.Seconds()).Match(
                s => Values(s).SequenceEqual([5.0, 6.0]), "a bound chart FOLLOWS its feed"))
            .Should().Equal(5, 6);

        // NEGATIVE CONTROL: the other entry was not touched, so its binding must not move.
        await other.Where(s => Values(s).SequenceEqual([5.0, 6.0])).Should().NotEmit(TimeSpan.FromSeconds(1),
            "a pointer to a different entry does not see the change — the follow above is the binding, not any emission");
    }
}
