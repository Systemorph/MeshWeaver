using System;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The bookkeeping half of Systemorph/MeshWeaver#4500 — <see cref="NodePageProvenance"/> on a
/// <see cref="LayoutDefinition"/>.
///
/// <para>The issue was not that three pages lacked a provenance line. It was that "somebody weighed
/// this and decided against it" and "nobody thought about it" were the SAME observation from
/// outside: an absence. A verdict recorded beside the renderer makes them two different sentences,
/// and <c>WithNodePage</c>'s default makes the second one produce a correct page anyway.</para>
///
/// <para>🚨 The assertion that carries the design is
/// <see cref="A_verdict_does_not_survive_the_renderer_it_describes"/>. Everything else here is
/// bookkeeping; that one is what stops the record from lying. A node type taking over its own
/// landing page is EXACTLY the move that loses the line, and if the framework's own
/// "the page renders it" verdict survived that takeover, every check downstream would pass having
/// measured a renderer that is no longer registered — the shape AGENTS.md calls a gate that tests
/// its own inputs.</para>
/// </summary>
public class NodePageProvenanceVerdictTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "Overview";

    private static IObservable<UiControl?> APage(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(Controls.Markdown("a page"));

    private static IObservable<UiControl?> AnotherPage(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(Controls.Markdown("another page"));

    private LayoutDefinition Layout() => new(GetHost());

    [HubFact]
    public void A_landing_page_registered_without_an_opinion_carries_the_line()
    {
        // The inversion the issue asked for: the DEFAULT is provenance, so a page written by
        // someone who never thought about it ships correct rather than silently incomplete.
        var layout = Layout().WithNodePage(Area, APage);

        layout.GetNodePageProvenance(Area)!.Kind
            .Should().Be(NodePageProvenanceKind.FrameworkSupplied);
    }

    [HubFact]
    public void A_page_that_draws_its_own_header_says_so()
    {
        var layout = Layout().WithNodePage(Area, APage, NodePageProvenance.RenderedByThePage);

        layout.GetNodePageProvenance(Area)!.Kind
            .Should().Be(NodePageProvenanceKind.RenderedByThePage);
    }

    [HubFact]
    public void A_decline_carries_the_sentence_the_next_reader_gets()
    {
        const string Because = "an Activity reports the run's own start and end instead";
        var layout = Layout().WithNodePage(Area, APage, NodePageProvenance.Declined(Because));

        var verdict = layout.GetNodePageProvenance(Area)!;
        verdict.Kind.Should().Be(NodePageProvenanceKind.Declined);
        verdict.Reason.Should().Be(Because);
    }

    [HubFact]
    public void An_empty_decline_is_refused()
    {
        // A decline with no reason records that somebody decided without recording what — which
        // leaves the next reader exactly where the issue found them, staring at an absence.
        Assert.Throws<ArgumentException>(() => NodePageProvenance.Declined("  "));
    }

    [HubFact]
    public void A_page_that_never_declared_anything_has_no_verdict()
    {
        // Null is not Declined. "Nobody decided" has to stay distinguishable from "decided no",
        // or the guard that reads this cannot tell a considered page from a forgotten one.
        var layout = Layout().WithView(Area, APage);

        layout.GetNodePageProvenance(Area).Should().BeNull();
    }

    [HubFact]
    public void A_verdict_does_not_survive_the_renderer_it_describes()
    {
        // 🚨 The whole mechanism in one assertion. A node type takes over its landing page with a
        // bare WithView — the move that loses the provenance line — and the framework's own
        // verdict for that area must go with the renderer it described. Delete the
        // `NodePages.Remove(area)` in LayoutDefinition.WithNamedRenderer and this is the test that
        // reddens: the takeover would inherit "the page renders it" from the page it replaced.
        var layout = Layout()
            .WithNodePage(Area, APage, NodePageProvenance.RenderedByThePage)
            .WithView(Area, AnotherPage);

        layout.GetNodePageProvenance(Area).Should().BeNull(
            "a verdict describes ONE renderer, and this area's renderer has been replaced");
    }

    [HubFact]
    public void Replacing_a_page_through_WithNodePage_records_the_new_pages_verdict()
    {
        // The other direction of the same invariant: the clear happens on registration and the new
        // verdict is written after it, so a deliberate takeover keeps a correct record.
        var layout = Layout()
            .WithNodePage(Area, APage, NodePageProvenance.RenderedByThePage)
            .WithNodePage(Area, AnotherPage);

        layout.GetNodePageProvenance(Area)!.Kind
            .Should().Be(NodePageProvenanceKind.FrameworkSupplied);
    }

    [HubFact]
    public void A_verdict_on_one_area_says_nothing_about_another()
    {
        var layout = Layout()
            .WithNodePage(Area, APage)
            .WithView("Edit", AnotherPage);

        layout.GetNodePageProvenance(Area).Should().NotBeNull();
        layout.GetNodePageProvenance("Edit").Should().BeNull();
    }
}
