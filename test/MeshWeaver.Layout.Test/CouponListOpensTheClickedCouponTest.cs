using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The REFERENCE conversion for row-scoped actions (Doc/GUI/DataBinding → "Row-scoped actions"): the
/// coupon admin list is a fed grid (a template, data later), and its per-coupon Open button — which
/// the first conversion had to drop for "click the row" — is back as ONE button in a template column.
/// Clicking the button in row k opens coupon k, also after the list has refreshed underneath.
/// </summary>
public class CouponListOpensTheClickedCouponTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "Coupons";

    private static CouponAdminSettingsTab.CouponRow Row(string code) => new() { Code = code };

    private static readonly ImmutableList<CouponAdminSettingsTab.CouponRow> Initial =
        [Row("ALPHA"), Row("BRAVO"), Row("CHARLIE")];

    private readonly BehaviorSubject<IEnumerable<CouponAdminSettingsTab.CouponRow>> rows = new(Initial);

    /// <summary>Navigations the client received, hot: each click subscribes before it submits.</summary>
    private readonly Subject<NavigationRequest> navigations = new();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(CouponAdminSettingsTab.CouponRow))
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout.WithView(Area,
                (host, _) => CouponAdminSettingsTab.CouponGrid(host, rows)));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient(d => d)
            .WithHandler<NavigationRequest>((_, delivery) =>
            {
                navigations.OnNext(delivery.Message);
                return delivery.Processed();
            });

    /// <summary>Row k's Open button opens coupon k, for every k — and the button is labelled with the
    /// row's own code, bound to the grid row.</summary>
    [HubFact]
    public async Task TheOpenButtonInRowKOpensCouponK()
    {
        var (stream, buttonArea) = await OpenGrid();
        var rendered = await RenderedRows(stream, Initial.Count);

        for (var k = 0; k < Initial.Count; k++)
        {
            var uri = await ClickAndReadNavigation(stream, buttonArea, AsTheGridRendersIt(rendered[k]));
            uri.Should().Be($"/{CouponAdminSettingsTab.CouponsNamespace}/{Initial[k].Code}",
                $"the Open button in row {k} was clicked");
        }
    }

    /// <summary>The coupons refresh between the render and the click — a coupon created above, one
    /// deleted — and the click still opens the coupon that was clicked.</summary>
    [HubFact]
    public async Task AfterARefreshTheClickStillOpensTheCouponThatWasClicked()
    {
        var (stream, buttonArea) = await OpenGrid();
        var clicked = AsTheGridRendersIt((await RenderedRows(stream, Initial.Count))[1]); // BRAVO

        rows.OnNext([Row("AARDVARK"), Row("ABACUS"), Row("CHARLIE")]); // BRAVO gone, slot 1 is ABACUS
        await RenderedRows(stream, 3, first: "AARDVARK");

        var uri = await ClickAndReadNavigation(stream, buttonArea, clicked);
        uri.Should().Be($"/{CouponAdminSettingsTab.CouponsNamespace}/BRAVO",
            "the person clicked BRAVO; the row that has since moved into its slot is not theirs to open");
    }

    /// <summary>The code is client input and is escaped as ONE path segment: a code carrying a
    /// separator and a space opens that single escaped segment, never a deeper path.</summary>
    [HubFact]
    public async Task ACodeThatNeedsEscapingOpensOneEscapedSegment()
    {
        var (stream, buttonArea) = await OpenGrid();
        await RenderedRows(stream, Initial.Count);

        rows.OnNext([Row("A/B C")]);
        var rendered = await RenderedRows(stream, 1, first: "A/B C");

        var uri = await ClickAndReadNavigation(stream, buttonArea, AsTheGridRendersIt(rendered[0]));
        uri.Should().Be($"/{CouponAdminSettingsTab.CouponsNamespace}/A%2FB%20C",
            "the code's '/' and ' ' are escaped inside its one segment, so it cannot name a deeper path");
    }

    /// <summary>NEGATIVE CONTROL: the same button clicked with no row opens nothing — the coupon comes
    /// from the row on the click and from nothing else.</summary>
    [HubFact]
    public async Task AClickWithNoRowOpensNothing()
    {
        var (stream, buttonArea) = await OpenGrid();
        await RenderedRows(stream, Initial.Count);

        var next = new ReplaySubject<NavigationRequest>(1);
        using var _ = navigations.Subscribe(next);
        Submit(stream, buttonArea, row: null);

        await next.Should().NotEmit(500.Milliseconds(),
            "with no row the action cannot know which coupon, and must not guess",
            TestContext.Current.CancellationToken);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(ISynchronizationStream<JsonElement> Stream, string ButtonArea)> OpenGrid()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        var buttonArea = $"{Area}/{DataGridControl.TemplateColumnArea(0)}";
        var button = await stream.GetControlStream(buttonArea).Should().Within(10.Seconds()).Match(
            c => c is ButtonControl,
            "the Code column's button is rendered into the template column's area",
            TestContext.Current.CancellationToken);
        button.Should().BeOfType<ButtonControl>().Subject.Data.Should().Be(new ContextProperty("code"),
            "each row's button is labelled with that row's code, bound to the grid row");
        return (stream, buttonArea);
    }

    private static async Task<ImmutableArray<JsonElement>> RenderedRows(
        ISynchronizationStream<JsonElement> stream, int count, string? first = null)
    {
        var all = await stream.GetDataStream<JsonElement>(
                new JsonPointerReference(LayoutAreaReference.GetDataPointer("couponList")))
            .Should().Within(10.Seconds()).Match(
                r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == count
                     && (first is null || r[0].GetProperty("code").GetString() == first),
                "the client mirror holds the coupon rows the grid renders",
                TestContext.Current.CancellationToken);
        return [.. all.EnumerateArray().Select(r => r.Clone())];
    }

    /// <summary>The grid client's row: a JSON object with no pointer (a grid sorts and pages).</summary>
    private static RowContext AsTheGridRendersIt(JsonElement row) => new() { Value = JsonObject.Create(row) };

    private static void Submit(ISynchronizationStream<JsonElement> stream, string area, RowContext? row)
        => stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId) { Row = row },
            actingUser: null, onRefused: null, onAccepted: null);

    private async Task<string> ClickAndReadNavigation(
        ISynchronizationStream<JsonElement> stream, string area, RowContext row)
    {
        var next = new ReplaySubject<NavigationRequest>(1);
        using var _ = navigations.Subscribe(next);
        Submit(stream, area, row);
        var request = await next.Should().Within(10.Seconds()).Emit(
            "the Open button navigates to the coupon", TestContext.Current.CancellationToken);
        return request is { } navigation
            ? navigation.Uri
            : throw new InvalidOperationException("The Open button navigated nowhere.");
    }
}
