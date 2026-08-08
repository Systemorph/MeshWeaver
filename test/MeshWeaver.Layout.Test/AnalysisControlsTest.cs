using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the standard analysis views (#745): the control SHAPE each factory + fluent builder produces,
/// the wire shape it round-trips as, and — the substance — the pure geometry the renderers project.
///
/// <para>The arithmetic lives in <see cref="TowerControl.Layout"/> and
/// <see cref="ComparisonBarsControl.Layout"/> precisely so it can be pinned HERE rather than only
/// through a renderer: the Blazor and React views are declarative projections of these numbers
/// (<c>clients/react/src/controls/analysis.test.tsx</c> mirrors these cases against the TS port), so
/// a tower that stacks wrong or a comparison that lies about an absent side fails as a unit test, in
/// every client at once.</para>
/// </summary>
public class AnalysisControlsTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    #region KPI strip

    [Fact]
    public void KpiStrip_factory_carries_the_tiles_in_order()
    {
        var strip = Controls.KpiStrip(
            new KpiItem("Premium", "12.4m"),
            new KpiItem("Combined ratio", "94.1%", "before commission"));

        var items = AnalysisRows.Resolve<KpiItem>(strip.Items);
        items.Should().HaveCount(2);
        items[0].Should().Be(new KpiItem("Premium", "12.4m", null));
        items[1].Hint.Should().Be("before commission");
    }

    [Fact]
    public void KpiStrip_builders_are_non_destructive()
    {
        var original = Controls.KpiStrip(new KpiItem("A", "1"));
        var widened = original.WithMinTileWidth("240px");

        widened.MinTileWidth.Should().Be("240px");
        widened.Items.Should().BeSameAs(original.Items);
        original.MinTileWidth.Should().BeNull(because: "records are immutable — With* returns a copy");

        var rebound = original.WithItems(new JsonPointerReference("/data/kpis"));
        rebound.Items.Should().BeOfType<JsonPointerReference>(
            because: "a strip must be bindable to a live data section, not only to literal tiles");
    }

    /// <summary>
    /// The strip must compose like any other leaf — no layout mechanism of its own beyond its tiles.
    /// </summary>
    [Fact]
    public void KpiStrip_sits_inside_a_stack_like_any_other_control()
    {
        var stack = Controls.Stack
            .WithView(Controls.Title("Economics", 2), "Title")
            .WithView(Controls.KpiStrip(new KpiItem("Premium", "12.4m")), "Kpis")
            .WithView(Controls.DataGrid(new[] { new { Name = "x" } }), "Detail");

        stack.Areas.Should().HaveCount(3);
        Controls.KpiStrip(new KpiItem("A", "1")).Should().BeAssignableTo<UiControl>();
    }

    #endregion

    #region Row resolution

    /// <summary>
    /// A row property may be handed the rows directly OR bound — both must resolve to the same rows,
    /// and anything unreadable must resolve EMPTY so the view shows its "nothing here" state rather
    /// than a half-drawn frame.
    /// </summary>
    [Fact]
    public void Rows_resolve_from_a_literal_list_and_from_bound_json_alike()
    {
        var literal = ImmutableList.Create(new KpiItem("Premium", "12.4m", "gross"));
        AnalysisRows.Resolve<KpiItem>(literal).Should().BeSameAs(literal);
        AnalysisRows.Resolve<KpiItem>(literal.ToArray()).Should().HaveCount(1);

        var json = JsonSerializer.SerializeToElement(literal, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        AnalysisRows.Resolve<KpiItem>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Single().Should().Be(new KpiItem("Premium", "12.4m", "gross"));

        AnalysisRows.Resolve<KpiItem>(null).Should().BeEmpty();
        AnalysisRows.Resolve<KpiItem>("not rows at all").Should().BeEmpty();
        AnalysisRows.Resolve<KpiItem>(new JsonPointerReference("/data/unresolved")).Should().BeEmpty();
    }

    #endregion

    #region Tower

    [Fact]
    public void Tower_factory_takes_bands_and_currency()
    {
        var tower = Controls.Tower(
            ImmutableList.Create(new TowerBand("Layer 1", "7m xs 3m", 3_000_000, 7_000_000, 0.5)),
            "EUR");

        tower.Currency.Should().Be("EUR");
        AnalysisRows.Resolve<TowerBand>(tower.Bands).Should().ContainSingle();

        var configured = tower.WithHeight("520px").WithFormat("C0").WithRetentionLabel("cedent retains");
        configured.Height.Should().Be("520px");
        configured.Format.Should().Be("C0");
        configured.RetentionLabel.Should().Be("cedent retains");
        configured.Bands.Should().BeSameAs(tower.Bands);
        tower.Height.Should().BeNull(because: "records are immutable — With* returns a copy");
    }

    /// <summary>
    /// The defining property of the drawing: consecutive layers TOUCH. Layer 2 attaching where
    /// layer 1 exhausts must start exactly where layer 1 ends — no gap, no overlap.
    /// </summary>
    [Fact]
    public void Tower_stacks_consecutive_layers_edge_to_edge()
    {
        var layout = TowerControl.Layout(
        [
            new TowerBand("Layer 2", "15m xs 10m", 10_000_000, 15_000_000),
            new TowerBand("Layer 1", "7m xs 3m", 3_000_000, 7_000_000),
        ])!;

        layout.Top.Should().Be(25_000_000);
        layout.Retention.Should().Be(3_000_000);

        // Ordered by attachment regardless of input order.
        layout.Bands.Select(b => b.Band.Label).Should().ContainInOrder("Layer 1", "Layer 2");

        var first = layout.Bands[0];
        var second = layout.Bands[1];
        first.BottomPercent.Should().BeApproximately(12, 1e-9);
        first.HeightPercent.Should().BeApproximately(28, 1e-9);
        // Layer 1 exhausts at 10m and layer 2 attaches at 10m → the blocks share an edge.
        (first.BottomPercent + first.HeightPercent).Should().BeApproximately(second.BottomPercent, 1e-9);
        (second.BottomPercent + second.HeightPercent).Should().BeApproximately(100, 1e-9);
    }

    [Fact]
    public void Tower_retention_is_the_lowest_attachment_and_never_negative()
    {
        TowerControl.Layout([new TowerBand("Ground up", "10m xs 0", 0, 10_000_000)])!
            .Retention.Should().Be(0);

        TowerControl.Layout([new TowerBand("Odd", "10m", -5, 10)])!
            .Retention.Should().Be(0, because: "a negative attachment is not a retention below zero");
    }

    /// <summary>Nothing to draw is said in words by the view — the layout returns null, not an empty frame.</summary>
    [Fact]
    public void Tower_layout_is_null_when_there_is_nothing_honest_to_draw()
    {
        TowerControl.Layout(null).Should().BeNull();
        TowerControl.Layout([]).Should().BeNull();
        TowerControl.Layout([new TowerBand("Empty", "", 0, 0)]).Should()
            .BeNull(because: "a tower with no positive exhaustion point has no axis");
    }

    [Theory]
    [InlineData("Acme/Deal/Layer1", "/Acme/Deal/Layer1")]
    [InlineData("/Acme/Deal/Layer1", "/Acme/Deal/Layer1")]
    [InlineData("https://example.com/x", "https://example.com/x")]
    [InlineData("  Acme/Deal  ", "/Acme/Deal")]
    public void Tower_band_href_is_normalized_to_a_navigable_target(string input, string expected)
        => TowerControl.NavigableHref(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tower_band_without_href_renders_unlinked(string? input)
        => TowerControl.NavigableHref(input).Should().BeNull();

    #endregion

    #region Comparison bars

    [Fact]
    public void ComparisonBars_factory_and_builders_produce_the_expected_control()
    {
        var bars = Controls
            .ComparisonBars(
                new ComparisonPair("Paid", 120_000, 118_500),
                new ComparisonPair("Outstanding", 90_000, null))
            .WithLegends("reported", "ours")
            .WithFormat("C0");

        bars.LeftLegend.Should().Be("reported");
        bars.RightLegend.Should().Be("ours");
        bars.Format.Should().Be("C0");
        AnalysisRows.Resolve<ComparisonPair>(bars.Pairs).Should().HaveCount(2);
        bars.AbsentText.Should().BeNull(because: "null means the view renders its localized default");
    }

    /// <summary>Both series are sized against ONE scale — that is what makes the bars comparable.</summary>
    [Fact]
    public void ComparisonBars_sizes_both_series_against_one_shared_scale()
    {
        var layout = ComparisonBarsControl.Layout(
        [
            new ComparisonPair("Paid", 100, 50),
            new ComparisonPair("Outstanding", 200, 200),
        ])!;

        layout.Max.Should().Be(200);
        layout.Rows[0].LeftPercent!.Value.Should().BeApproximately(50, 1e-9);
        layout.Rows[0].RightPercent!.Value.Should().BeApproximately(25, 1e-9);
        layout.Rows[1].LeftPercent!.Value.Should().BeApproximately(100, 1e-9);
        layout.Rows[1].RightPercent!.Value.Should().BeApproximately(100, 1e-9);
    }

    /// <summary>
    /// THE point of the control. An absent side stays null so the view can say "not on this side";
    /// a present zero is a figure and gets a (minimum-visible) bar. Collapsing the two is the lie
    /// this control exists to prevent.
    /// </summary>
    [Fact]
    public void ComparisonBars_keeps_absent_and_zero_distinguishable()
    {
        var layout = ComparisonBarsControl.Layout(
        [
            new ComparisonPair("Only ours", null, 400),
            new ComparisonPair("Zero on the left", 0, 400),
        ])!;

        layout.Rows[0].LeftPercent.Should().BeNull(because: "absent is not a value");
        layout.Rows[1].LeftPercent.Should().NotBeNull(because: "zero IS a value the book reports");
        layout.Rows[1].LeftPercent!.Value.Should()
            .BeGreaterThan(0, because: "a reported figure keeps a visible sliver");
        layout.Rows[1].LeftPercent!.Value.Should().BeLessThan(1);
    }

    [Fact]
    public void ComparisonBars_layout_is_null_when_no_side_carries_a_value()
    {
        ComparisonBarsControl.Layout(null).Should().BeNull();
        ComparisonBarsControl.Layout([]).Should().BeNull();
        ComparisonBarsControl.Layout([new ComparisonPair("Nothing", null, null)]).Should().BeNull();
        ComparisonBarsControl.Layout([new ComparisonPair("All zero", 0, 0)]).Should()
            .BeNull(because: "there is no positive scale to size the bars against");
    }

    #endregion

    #region Wire shape

    /// <summary>
    /// Each control round-trips with the SHORT <c>$type</c> discriminator, and its rows survive with
    /// it — including a null side, which must NOT come back as a zero.
    /// </summary>
    [Fact]
    public void Analysis_controls_round_trip_through_the_hub_serializer()
    {
        var client = GetClient();
        var options = client.JsonSerializerOptions;

        var strip = RoundTrip<KpiStripControl>(
            Controls.KpiStrip(new KpiItem("Premium", "12.4m", "gross")), nameof(KpiStripControl));
        AnalysisRows.Resolve<KpiItem>(strip.Items, options)
            .Single().Should().Be(new KpiItem("Premium", "12.4m", "gross"));

        var tower = RoundTrip<TowerControl>(
            Controls.Tower(
                    ImmutableList.Create(
                        new TowerBand("Layer 1", "7m xs 3m", 3_000_000, 7_000_000, 0.5, "Acme/Layer1")),
                    "EUR")
                .WithHeight("520px"),
            nameof(TowerControl));
        tower.Currency!.ToString().Should().Be("EUR");
        tower.Height!.ToString().Should().Be("520px");
        AnalysisRows.Resolve<TowerBand>(tower.Bands, options)
            .Single().Should().Be(new TowerBand("Layer 1", "7m xs 3m", 3_000_000, 7_000_000, 0.5, "Acme/Layer1"));

        var bars = RoundTrip<ComparisonBarsControl>(
            Controls.ComparisonBars(new ComparisonPair("Outstanding", 90_000, null))
                .WithLegends("reported", "ours"),
            nameof(ComparisonBarsControl));
        bars.LeftLegend!.ToString().Should().Be("reported");
        AnalysisRows.Resolve<ComparisonPair>(bars.Pairs, options)
            .Single().Right.Should().BeNull(
                because: "an absent side must survive the wire as absent, not as 0");

        T RoundTrip<T>(UiControl control, string expectedDiscriminator) where T : UiControl
        {
            var json = JsonSerializer.Serialize<UiControl>(control, options);
            Output.WriteLine($"{expectedDiscriminator}: {json}");
            using (var doc = JsonDocument.Parse(json))
                doc.RootElement.GetProperty("$type").GetString().Should().Be(expectedDiscriminator);
            return JsonSerializer.Deserialize<UiControl>(json, options).Should().BeOfType<T>().Subject;
        }
    }

    #endregion
}
