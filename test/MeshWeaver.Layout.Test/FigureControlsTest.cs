using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the two figure controls that replaced hand-built SVG in node code — the
/// <see cref="WaterfallControl"/> (a bridge from an opening amount through signed deltas to a result)
/// and the <see cref="HierarchyControl"/> (a top-down tree of boxes): the factory shape, the wire
/// shape, and — the substance — the pure geometry and structure the renderers project.
/// </summary>
public class FigureControlsTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    #region Waterfall

    private static ImmutableList<WaterfallStep> Bridge() =>
    [
        WaterfallStep.Total("Premium", 100),
        WaterfallStep.Delta("Losses", -60),
        WaterfallStep.Delta("Costs", -15),
        WaterfallStep.Total("Result", 25),
    ];

    /// <summary>
    /// The defining property: every delta hangs from the running level, so each bar starts exactly
    /// where the previous one ended, and the result stands on the axis again.
    /// </summary>
    [Fact]
    public void Waterfall_deltas_hang_from_the_running_level()
    {
        var layout = WaterfallControl.Layout(Bridge())!;

        layout.Min.Should().Be(0);
        layout.Max.Should().Be(100);
        layout.ZeroPercent.Should().Be(0);

        var (premium, losses, costs, result) = (layout.Bars[0], layout.Bars[1], layout.Bars[2], layout.Bars[3]);
        premium.BottomPercent.Should().Be(0);
        premium.HeightPercent.Should().BeApproximately(100, 1e-9);

        // Losses hang from 100 down to 40.
        losses.From.Should().Be(100);
        losses.To.Should().Be(40);
        losses.BottomPercent.Should().BeApproximately(40, 1e-9);
        losses.HeightPercent.Should().BeApproximately(60, 1e-9);

        // Costs start where losses ended.
        costs.From.Should().Be(losses.To);
        (costs.BottomPercent + costs.HeightPercent).Should().BeApproximately(losses.BottomPercent, 1e-9);

        // The result stands on zero.
        result.From.Should().Be(0);
        result.BottomPercent.Should().Be(0);
        result.HeightPercent.Should().BeApproximately(25, 1e-9);

        // The connector after each bar runs at the running level.
        layout.Bars.Select(b => b.LevelPercent).Should().Equal(100, 40, 25, 25);
    }

    [Fact]
    public void Waterfall_tones_and_signs_follow_the_kind_and_the_sign()
    {
        var layout = WaterfallControl.Layout(Bridge())!;
        layout.Bars.Select(b => b.Tone).Should().Equal(
            WaterfallTone.Total, WaterfallTone.Decrease, WaterfallTone.Decrease, WaterfallTone.ResultPositive);
        layout.Bars.Select(b => b.Signed).Should().Equal(false, true, true, false);

        var gain = WaterfallControl.Layout([WaterfallStep.Total("Start", 10), WaterfallStep.Delta("Gain", 5)])!;
        gain.Bars[1].Tone.Should().Be(WaterfallTone.Increase);
        gain.Bars[1].Tone.Should().NotBe(WaterfallTone.ResultPositive,
            because: "only a closing TOTAL is a result — a closing delta is still a change");
    }

    /// <summary>A loss-making result goes below zero: the axis extends down to it and zero moves up.</summary>
    [Fact]
    public void Waterfall_axis_includes_a_negative_result()
    {
        var layout = WaterfallControl.Layout(
        [
            WaterfallStep.Total("Premium", 100),
            WaterfallStep.Delta("Losses", -130),
            WaterfallStep.Total("Result", -30),
        ])!;

        layout.Min.Should().Be(-30);
        layout.Max.Should().Be(100);
        layout.ZeroPercent.Should().BeApproximately(30.0 / 130 * 100, 1e-9);

        var result = layout.Bars[2];
        result.Tone.Should().Be(WaterfallTone.ResultNegative);
        result.BottomPercent.Should().Be(0);
        (result.BottomPercent + result.HeightPercent).Should().BeApproximately(layout.ZeroPercent, 1e-9,
            because: "a negative total hangs from zero down to its level");

        var losses = layout.Bars[1];
        losses.BottomPercent.Should().Be(0);
        losses.HeightPercent.Should().BeApproximately(100, 1e-9, because: "the delta crosses the axis from 100 to −30");
    }

    /// <summary>A total RESETS the running level (a subtotal), and an unknown kind is drawn as a delta.</summary>
    [Fact]
    public void Waterfall_a_subtotal_resets_the_level_and_an_unknown_kind_is_a_delta()
    {
        var layout = WaterfallControl.Layout(
        [
            WaterfallStep.Total("Gross", 200),
            WaterfallStep.Delta("Ceded", -50),
            WaterfallStep.Total("Net", 150),
            new WaterfallStep("Something new", -10, "future-kind"),
        ])!;

        layout.Bars[2].From.Should().Be(0);
        layout.Bars[3].From.Should().Be(150, because: "the delta after a subtotal hangs from the subtotal");
        layout.Bars[3].Tone.Should().Be(WaterfallTone.Decrease);
    }

    [Fact]
    public void Waterfall_a_tiny_amount_keeps_a_sliver_and_a_zero_draws_nothing()
    {
        var layout = WaterfallControl.Layout(
        [
            WaterfallStep.Total("Premium", 1_000_000),
            WaterfallStep.Delta("Fee", -1),
            WaterfallStep.Delta("Nothing", 0),
        ])!;

        layout.Bars[1].HeightPercent.Should().BeGreaterThan(0.4, because: "a present figure stays visible");
        (layout.Bars[1].BottomPercent + layout.Bars[1].HeightPercent).Should().BeLessThanOrEqualTo(100,
            because: "the sliver never pushes a bar off the top of the axis");
        layout.Bars[2].HeightPercent.Should().Be(0);
    }

    /// <summary>Nothing to draw is said in words by the view — the layout returns null, not an empty frame.</summary>
    [Fact]
    public void Waterfall_layout_is_null_when_there_is_nothing_honest_to_draw()
    {
        WaterfallControl.Layout(null).Should().BeNull();
        WaterfallControl.Layout([]).Should().BeNull();
        WaterfallControl.Layout([WaterfallStep.Total("Zero", 0), WaterfallStep.Delta("None", 0)]).Should().BeNull();
        WaterfallControl.Layout([WaterfallStep.Total("Broken", double.NaN)]).Should()
            .BeNull(because: "a non-finite amount has no place on an axis");
    }

    [Fact]
    public void Waterfall_factory_and_builders_carry_their_values()
    {
        var control = Controls.Waterfall(Bridge().ToArray()).WithCurrency("EUR").WithFormat("N0").WithHeight("320px");
        AnalysisRows.Resolve<WaterfallStep>(control.Steps).Should().Equal(Bridge());
        control.Currency.Should().Be("EUR");
        control.Format.Should().Be("N0");
        control.Height.Should().Be("320px");
    }

    #endregion

    #region Hierarchy

    private static ImmutableList<HierarchyNode> Deal() =>
    [
        new("deal", null, "MTPL 2026", "Submission · 2 options", "Acme/MTPL2026", HierarchyEmphasis.Strong),
        new("o1", "deal", "Layer 1", "7m xs 3m", "Acme/MTPL2026/Layer1", HierarchyEmphasis.Current),
        new("o2", "deal", "Layer 2", "15m xs 10m", "Acme/MTPL2026/Layer2"),
        new("s1", "o1", "Motor", "Section"),
        new("s2", "o1", "Liability", "Section"),
    ];

    [Fact]
    public void Hierarchy_builds_the_tree_from_flat_nodes_in_input_order()
    {
        var layout = HierarchyControl.Layout(Deal())!;

        layout.Depth.Should().Be(3);
        var root = layout.Roots.Should().ContainSingle().Subject;
        root.Node.Id.Should().Be("deal");
        root.Depth.Should().Be(0);
        root.Children.Select(c => c.Node.Id).Should().Equal("o1", "o2");
        root.Children[0].Children.Select(c => c.Node.Title).Should().Equal("Motor", "Liability");
        root.Children[0].Children.Should().OnlyContain(c => c.Depth == 2);
        root.Children[1].Children.Should().BeEmpty();
    }

    [Fact]
    public void Hierarchy_an_unknown_parent_makes_a_root_and_a_duplicate_keeps_the_first()
    {
        var layout = HierarchyControl.Layout(
        [
            new("a", null, "A"),
            new("b", "missing", "B"),
            new("a", null, "A again"),
            new("self", "self", "Self-parented"),
        ])!;

        layout.Roots.Select(r => r.Node.Title).Should().Equal("A", "B", "Self-parented");
        layout.Depth.Should().Be(1);
    }

    /// <summary>
    /// A cycle has no root to hang from. It must still be drawn — every node exactly once — and must
    /// never send a renderer into an endless recursion.
    /// </summary>
    [Fact]
    public void Hierarchy_breaks_a_cycle_at_its_first_node_and_draws_every_node_once()
    {
        var layout = HierarchyControl.Layout(
        [
            new("x", "y", "X"),
            new("y", "x", "Y"),
            new("z", "y", "Z"),
        ])!;

        var root = layout.Roots.Should().ContainSingle().Subject;
        root.Node.Id.Should().Be("x");
        static IEnumerable<HierarchyTreeNode> All(HierarchyTreeNode n) => n.Children.SelectMany(All).Prepend(n);
        All(root).Select(n => n.Node.Id).Order().Should().Equal("x", "y", "z");
        All(root).Select(n => n.Node.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Hierarchy_layout_is_null_when_there_are_no_nodes()
    {
        HierarchyControl.Layout(null).Should().BeNull();
        HierarchyControl.Layout([]).Should().BeNull();
    }

    /// <summary>The current node is never a link — it would only open the page already open.</summary>
    [Fact]
    public void Hierarchy_links_every_node_but_the_current_one()
    {
        var nodes = Deal();
        HierarchyControl.LinkOf(nodes[0]).Should().Be("/Acme/MTPL2026");
        HierarchyControl.LinkOf(nodes[1]).Should().BeNull();
        HierarchyControl.LinkOf(nodes[2]).Should().Be("/Acme/MTPL2026/Layer2");
        HierarchyControl.LinkOf(nodes[3]).Should().BeNull(because: "a node without an href is unlinked");
        HierarchyControl.IsEmphasis(nodes[0], "STRONG").Should().BeTrue();
        HierarchyControl.IsEmphasis(nodes[2], HierarchyEmphasis.None).Should().BeTrue();
    }

    #endregion

    #region Wire shape

    /// <summary>Both controls round-trip with the SHORT <c>$type</c> discriminator, and their rows with them.</summary>
    [Fact]
    public void Figure_controls_round_trip_through_the_hub_serializer()
    {
        var options = GetClient().JsonSerializerOptions;

        var waterfall = RoundTrip<WaterfallControl>(
            Controls.Waterfall(Bridge().ToArray()).WithCurrency("EUR"), nameof(WaterfallControl));
        waterfall.Currency!.ToString().Should().Be("EUR");
        AnalysisRows.Resolve<WaterfallStep>(waterfall.Steps, options).Should().Equal(Bridge());

        var hierarchy = RoundTrip<HierarchyControl>(Controls.Hierarchy(Deal().ToArray()), nameof(HierarchyControl));
        AnalysisRows.Resolve<HierarchyNode>(hierarchy.Nodes, options).Should().Equal(Deal());

        T RoundTrip<T>(UiControl control, string expectedDiscriminator) where T : UiControl
        {
            var json = JsonSerializer.Serialize<UiControl>(control, options);
            Output.WriteLine($"{expectedDiscriminator}: {json}");
            using (var doc = JsonDocument.Parse(json))
                doc.RootElement.GetProperty("$type").GetString().Should().Be(expectedDiscriminator);
            return JsonSerializer.Deserialize<UiControl>(json, options).Should().BeOfType<T>().Subject;
        }
    }

    /// <summary>A bound row property (a JSON array off the sync stream) resolves to the same rows as a typed one.</summary>
    [Fact]
    public void Figure_rows_resolve_from_their_json_form()
    {
        var options = GetClient().JsonSerializerOptions;
        var json = JsonSerializer.SerializeToElement(Deal(), options);
        AnalysisRows.Resolve<HierarchyNode>(json, options).Should().Equal(Deal());
    }

    #endregion
}
