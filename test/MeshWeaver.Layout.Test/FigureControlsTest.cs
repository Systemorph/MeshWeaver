using System.Collections.Generic;
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

    /// <summary>A total RESETS the running level (a subtotal); a delta after it hangs from it.</summary>
    [Fact]
    public void Waterfall_a_subtotal_resets_the_level()
    {
        var layout = WaterfallControl.Layout(
        [
            WaterfallStep.Total("Gross", 200),
            WaterfallStep.Delta("Ceded", -50),
            WaterfallStep.Total("Net", 150),
            WaterfallStep.Delta("Fees", -10),
        ])!;

        layout.Bars[2].From.Should().Be(0);
        layout.Bars[3].From.Should().Be(150, because: "the delta after a subtotal hangs from the subtotal");
        layout.Bars[3].Tone.Should().Be(WaterfallTone.Decrease);
    }

    /// <summary>
    /// An unknown kind is never given a meaning: it does not move the running level, is not drawn,
    /// and is reported. A blank kind is the default delta.
    /// </summary>
    [Fact]
    public void Waterfall_an_unknown_kind_is_reported_never_counted()
    {
        var unknown = new WaterfallStep("Something new", -10, "future-kind");
        var layout = WaterfallControl.Layout(
        [
            WaterfallStep.Total("Gross", 200),
            unknown,
            new WaterfallStep("Blank kind", -50, ""),
            WaterfallStep.Total("Net", 150),
        ])!;

        layout.Unrecognized.Should().Equal(unknown);
        layout.Bars.Select(b => b.Step.Label).Should().Equal("Gross", "Blank kind", "Net");
        layout.Bars[1].From.Should().Be(200, because: "the unknown step did not move the running level");
        layout.Bars[1].Tone.Should().Be(WaterfallTone.Decrease);
        WaterfallControl.Layout([unknown]).Should().BeNull(because: "nothing recognized, nothing to draw");
    }

    /// <summary>Finite amounts whose axis overflows the double range have no honest geometry.</summary>
    [Fact]
    public void Waterfall_an_overflowing_axis_is_null_never_nan()
    {
        WaterfallControl.Layout([WaterfallStep.Total("Up", 1e308), WaterfallStep.Total("Down", -1e308)])
            .Should().BeNull(because: "max − min overflows to infinity");
        WaterfallControl.Layout([WaterfallStep.Total("Start", 1e308), WaterfallStep.Delta("More", 1e308)])
            .Should().BeNull(because: "the running level overflows");
        // The edge headroom must not overflow an axis that was finite before it was padded.
        WaterfallControl.Layout([WaterfallStep.Total("Max", double.MaxValue), WaterfallStep.Delta("Crumb", 1)])
            .Should().BeNull(because: "padding the top of a double.MaxValue axis overflows to infinity");
        WaterfallControl.Layout([WaterfallStep.Total("Min", -double.MaxValue), WaterfallStep.Delta("Crumb", -1)])
            .Should().BeNull(because: "padding the bottom of a −double.MaxValue axis overflows, never NaN percentages");
    }

    /// <summary>The visible minimum of a tiny NEGATIVE result stays below the baseline, on its own side.</summary>
    [Fact]
    public void Waterfall_a_tiny_negative_result_stays_below_zero()
    {
        var layout = WaterfallControl.Layout(
        [
            WaterfallStep.Total("Premium", 1_000_000),
            WaterfallStep.Delta("Losses", -1_000_001),
            WaterfallStep.Total("Result", -1),
        ])!;

        var result = layout.Bars[2];
        (result.BottomPercent + result.HeightPercent).Should().BeApproximately(layout.ZeroPercent, 1e-9,
            because: "a negative total hangs from zero downward — its top edge IS the baseline");
        result.BottomPercent.Should().BeGreaterThanOrEqualTo(0);
        result.Tone.Should().Be(WaterfallTone.ResultNegative);

        // At large magnitudes rounding makes from == to; the sliver still follows the SIGN of the step.
        var rounded = WaterfallControl.Layout([WaterfallStep.Total("Big", 1e16), WaterfallStep.Delta("Half", -5e15), WaterfallStep.Delta("Crumb", 0.25)])!;
        var crumb = rounded.Bars[2];
        crumb.Tone.Should().Be(WaterfallTone.Increase);
        crumb.BottomPercent.Should().BeApproximately(50, 1e-9, because: "a positive crumb grows UP from the running level at 50 %");
        crumb.HeightPercent.Should().BeGreaterThan(0);

        // A tiny step anchored on the axis EDGE still gets its sliver, on its own side: the axis gains
        // headroom instead of clamping the bar to zero height.
        var atTop = WaterfallControl.Layout([WaterfallStep.Total("Big", 1e16), WaterfallStep.Delta("Crumb", 0.25)])!;
        atTop.Bars[1].HeightPercent.Should().BeGreaterThan(0.4);
        atTop.Bars[1].BottomPercent.Should().BeApproximately(atTop.Bars[0].HeightPercent, 1e-9,
            because: "the crumb starts at the top of the base and grows UP");
        (atTop.Bars[1].BottomPercent + atTop.Bars[1].HeightPercent).Should().BeLessThanOrEqualTo(100);
        var atBottom = WaterfallControl.Layout([WaterfallStep.Total("Deep", -1e16), WaterfallStep.Delta("Crumb", -0.25)])!;
        atBottom.Bars[1].HeightPercent.Should().BeGreaterThan(0.4);
        (atBottom.Bars[1].BottomPercent + atBottom.Bars[1].HeightPercent).Should().BeApproximately(atBottom.Bars[0].BottomPercent, 1e-9,
            because: "the crumb hangs DOWN from the bottom of the deficit");
        atBottom.Bars[1].BottomPercent.Should().BeGreaterThanOrEqualTo(0);
        // The headroom only appears when a step needs it — an ordinary bridge keeps its exact axis.
        WaterfallControl.Layout(Bridge())!.Max.Should().Be(100);

        var tinyRise = WaterfallControl.Layout([WaterfallStep.Total("Base", 1_000_000), WaterfallStep.Delta("Fee", 1)])!;
        tinyRise.Bars[1].BottomPercent.Should().BeApproximately(tinyRise.Bars[0].HeightPercent, 1e-9,
            because: "a tiny rise grows upward from the running level (the top of the base)");
        (tinyRise.Bars[1].BottomPercent + tinyRise.Bars[1].HeightPercent).Should().BeLessThanOrEqualTo(100);
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

    /// <summary>
    /// A descendant listed BEFORE its cycle keeps its parent: the cycle is broken at its own first
    /// member, not at whichever unplaced node comes first.
    /// </summary>
    [Fact]
    public void Hierarchy_a_descendant_listed_before_its_cycle_keeps_its_parent()
    {
        var layout = HierarchyControl.Layout(
        [
            new("z", "y", "Z"),
            new("x", "y", "X"),
            new("y", "x", "Y"),
        ])!;

        var root = layout.Roots.Should().ContainSingle().Subject;
        root.Node.Id.Should().Be("x", because: "x is the cycle member that comes first in input order");
        var y = root.Children.Should().ContainSingle().Subject;
        y.Node.Id.Should().Be("y");
        y.Children.Select(c => c.Node.Id).Should().Equal("z");
    }

    /// <summary>Roots keep INPUT order even when one of them is a broken cycle.</summary>
    [Fact]
    public void Hierarchy_roots_keep_input_order_with_a_cycle()
    {
        var layout = HierarchyControl.Layout(
        [
            new("x", "y", "X"),
            new("y", "x", "Y"),
            new("z", null, "Z"),
        ])!;

        layout.Roots.Select(r => r.Node.Id).Should().Equal("x", "z");
        layout.Roots[0].Children.Select(c => c.Node.Id).Should().Equal("y");
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

    /// <summary>A bound href never becomes script: only site paths and http(s) URLs are links.</summary>
    [Theory]
    [InlineData("javascript://ignored/%0Aalert(1)")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example/x")]
    [InlineData("/\n/evil.example/x")]
    [InlineData("/\t/evil.example/x")]
    [InlineData("Acme\\Deal")]
    public void Hierarchy_an_unsafe_href_draws_the_box_unlinked(string href)
        => HierarchyControl.LinkOf(new HierarchyNode("n", null, "N", Href: href)).Should().BeNull();

    [Theory]
    [InlineData("Acme/Deal", "/Acme/Deal")]
    [InlineData("/Acme/Deal", "/Acme/Deal")]
    [InlineData("https://portal.example.com/Acme", "https://portal.example.com/Acme")]
    [InlineData("http://portal.example.com/Acme", "http://portal.example.com/Acme")]
    public void Hierarchy_a_safe_href_is_a_link(string href, string expected)
    {
        HierarchyControl.LinkOf(new HierarchyNode("n", null, "N", Href: href)).Should().Be(expected);
    }

    /// <summary>
    /// The general deal shape: a submission → 3 mutually exclusive options (one the favourite) → each
    /// option's 3 layers as a TOWER (stacked, highest first) → what a layer covers. Every node lands
    /// exactly once, under its own option, in input order, at its own depth.
    /// </summary>
    [Fact]
    public void Hierarchy_three_options_of_three_layers_each()
    {
        var nodes = new List<HierarchyNode>
        {
            new("sub", null, "MTPL 2026", "Submission · 3 options", "Acme/MTPL2026", HierarchyEmphasis.Strong)
                { Kind = HierarchyNodeKind.Group },
        };
        foreach (var o in new[] { 1, 2, 3 })
        {
            nodes.Add(new($"o{o}", "sub", $"Option {o}", "3 layers", $"Acme/MTPL2026/O{o}",
                o == 2 ? HierarchyEmphasis.Favourite : null)
            {
                Kind = HierarchyNodeKind.Option,
                Arrange = HierarchyArrangement.Stack,
                Relation = "option",
                Badges = o == 2 ? ["★ favourite"] : null,
            });
            foreach (var (l, terms) in new[] { (3, "20m xs 20m"), (2, "10m xs 10m"), (1, "5m xs 5m") })
                nodes.Add(new($"o{o}l{l}", $"o{o}", $"Layer {l}", $"{terms} · 3.1 % RoL")
                {
                    Kind = HierarchyNodeKind.ExcessOfLoss,
                    Figures = [new KpiItem("Our line", "2.5m"), new KpiItem("Our premium", "77.5k")],
                    Badges = l == 1 ? ["within authority"] : null,
                });
        }
        nodes.Add(new("o2l1s", "o2l1", "Motor TPL", "Section") { Kind = HierarchyNodeKind.Section });

        var layout = HierarchyControl.Layout(nodes)!;

        layout.Depth.Should().Be(4);
        var sub = layout.Roots.Should().ContainSingle().Subject;
        sub.Children.Select(c => c.Node.Title).Should().Equal("Option 1", "Option 2", "Option 3");
        sub.Children.Should().OnlyContain(o => o.Node.StacksChildren && o.Node.IsKind(HierarchyNodeKind.Option));
        sub.Children.Should().OnlyContain(o => o.Children.Count == 3);
        sub.Children[0].Children.Select(c => c.Node.Title).Should().Equal("Layer 3", "Layer 2", "Layer 1");
        HierarchyControl.IsEmphasis(sub.Children[1].Node, HierarchyEmphasis.Favourite).Should().BeTrue();
        sub.Children[1].Children[2].Children.Should().ContainSingle().Which.Node.Title.Should().Be("Motor TPL");
        static IEnumerable<HierarchyTreeNode> All(HierarchyTreeNode n) => n.Children.SelectMany(All).Prepend(n);
        All(sub).Should().HaveCount(nodes.Count, because: "1 submission + 3 options + 9 layers + 1 section, each once");
        All(sub).Select(n => n.Node.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// A chained programme, proportional → non-proportional: a 50 % quota share whose retention is
    /// protected by two XL layers. The kinds tell the renderer to style them apart; the relation is
    /// the edge label.
    /// </summary>
    [Fact]
    public void Hierarchy_a_quota_share_protected_by_two_xl_layers()
    {
        var layout = HierarchyControl.Layout(
        [
            new("qs", null, "Quota share 50 %", "30 % commission") { Kind = HierarchyNodeKind.Proportional, Arrange = HierarchyArrangement.Stack },
            new("xl2", "qs", "XL layer 2", "10m xs 10m · 2.4 % RoL") { Kind = HierarchyNodeKind.ExcessOfLoss, Relation = "on retention of" },
            new("xl1", "qs", "XL layer 1", "5m xs 5m · 6.1 % RoL") { Kind = HierarchyNodeKind.ExcessOfLoss, Relation = "on retention of" },
        ])!;

        var qs = layout.Roots.Should().ContainSingle().Subject;
        qs.Node.IsKind(HierarchyNodeKind.Proportional).Should().BeTrue();
        qs.Node.StacksChildren.Should().BeTrue();
        qs.Children.Select(c => c.Node.Title).Should().Equal("XL layer 2", "XL layer 1");
        qs.Children.Should().OnlyContain(c => c.Node.IsKind("EXCESS-OF-LOSS") && c.Node.Relation == "on retention of");
        layout.Depth.Should().Be(2);
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

    /// <summary>Kind, relation, arrangement, badges and figures survive the wire with the node.</summary>
    [Fact]
    public void Hierarchy_node_slots_survive_the_wire()
    {
        var options = GetClient().JsonSerializerOptions;
        var node = new HierarchyNode("l1", "o1", "Layer 1", "5m xs 5m")
        {
            Kind = HierarchyNodeKind.ExcessOfLoss,
            Relation = "inures to",
            Arrange = HierarchyArrangement.Stack,
            Badges = ["bound"],
            Figures = [new KpiItem("Our premium", "77.5k")],
        };
        var back = AnalysisRows.Resolve<HierarchyNode>(JsonSerializer.SerializeToElement(new[] { node }, options), options).Single();
        back.Kind.Should().Be(HierarchyNodeKind.ExcessOfLoss);
        back.Relation.Should().Be("inures to");
        back.StacksChildren.Should().BeTrue();
        back.Badges.Should().Equal("bound");
        back.Figures.Should().Equal(new KpiItem("Our premium", "77.5k"));
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
