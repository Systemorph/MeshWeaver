using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Documents — and pins — HOW an agent retrieves the list of layout areas ("regions") on a node,
/// and the one non-obvious rule about WHAT that list contains. Regions are the live areas you can
/// embed inline in markdown with the <c>@@</c> operator (e.g. <c>@@("area/Search")</c> for a
/// Space's contents catalog).
///
/// <para><b>How to retrieve.</b> Two equivalent surfaces, both exercised here:</para>
/// <list type="number">
///   <item><see cref="GetLayoutAreasRequest"/> → <see cref="LayoutAreasResponse.Areas"/> (typed message).</item>
///   <item><c>GetDataRequest(new UnifiedReference("layoutAreas:"))</c> — the wire behind the MCP /
///     agent call <c>Get('@Node/Path/layoutAreas/')</c>.</item>
/// </list>
/// <para>🔑 The reference is PLURAL <c>layoutAreas</c> (LISTS the areas). The SINGULAR
/// <c>area/{Name}</c> reference fetches ONE area's rendered payload. Listing is NOT <c>area/</c>.</para>
///
/// <para><b>What the list contains (the gotcha).</b> The listing returns only the areas with a
/// <em>visible</em> <see cref="LayoutAreaDefinition"/> — i.e. author-facing / catalog areas. The
/// STANDARD node regions (Overview, Search, Threads, Files, Chat, Edit, …) are all
/// <c>[Browsable(false)]</c>, so they are <b>embeddable via <c>@@("area/Search")</c> but do NOT
/// appear in the <c>layoutAreas</c> listing.</b> This test proves both halves: a custom visible
/// area shows up; the Browsable(false) standard areas do not.</para>
/// </summary>
public class LayoutAreaRetrievalTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string CustomVisibleArea = "SpaceDashboard";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout
                // The full standard node area set (Overview, Search, Threads, …) — every one
                // [Browsable(false)], so none are advertised in the layoutAreas listing.
                .AddDefaultLayoutAreas()
                // A custom, catalog-visible area (no [Browsable(false)]) WITH a description —
                // this is what an author registers to make an area discoverable + embeddable.
                .WithView(CustomVisibleArea, Dashboard,
                    a => a.WithDescription("A custom, catalog-visible layout area.")));

    private static IObservable<UiControl?> Dashboard(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(Controls.Markdown("# Space Dashboard"));

    [HubFact]
    public async Task GetLayoutAreasRequest_ListsVisibleAreasOnly()
    {
        GetHost();
        var client = GetClient();

        var response = await client
            .Observe(new GetLayoutAreasRequest(), o => o.WithTarget(CreateHostAddress()))
            .Should().Emit();

        var areas = response.Message.Areas.ToList();
        var names = areas.Select(a => a.Area).ToList();

        Output.WriteLine($"{areas.Count} VISIBLE layout area(s) returned by layoutAreas:");
        foreach (var a in areas.OrderBy(a => a.Area))
            Output.WriteLine($"  - {a.Area}: {a.Description ?? a.Title}");

        // A custom area with its own (non-Browsable-false) definition IS listed.
        names.Should().Contain(CustomVisibleArea);

        // The standard node regions are [Browsable(false)] — embeddable via @@("area/Search"),
        // but deliberately NOT advertised in the listing. This is the rule the skill documents.
        names.Should().NotContain("Overview");
        names.Should().NotContain("Search");
        names.Should().NotContain("Threads");
    }

    [HubFact]
    public async Task LayoutAreasUnifiedReference_MatchesTheTypedRequest()
    {
        GetHost();
        var client = GetClient();

        // This GetDataRequest is exactly what the agent's Get('@Node/Path/layoutAreas/') resolves to.
        var viaReference = await client
            .Observe(new GetDataRequest(new UnifiedReference("layoutAreas:")), o => o.WithTarget(CreateHostAddress()))
            .Should().Emit();
        viaReference.Message.Error.Should().BeNull();

        var referenceAreas = (viaReference.Message.Data as IEnumerable<LayoutAreaDefinition>)?
            .Select(a => a.Area).OrderBy(a => a).ToList();
        referenceAreas.Should().NotBeNull("the layoutAreas: reference returns a list of LayoutAreaDefinition");
        referenceAreas!.Should().Contain(CustomVisibleArea);

        var viaRequest = await client
            .Observe(new GetLayoutAreasRequest(), o => o.WithTarget(CreateHostAddress()))
            .Should().Emit();
        var requestAreas = viaRequest.Message.Areas.Select(a => a.Area).OrderBy(a => a).ToList();

        // Both surfaces resolve to the same underlying LayoutDefinition.AreaDefinitions — same list
        // (both projected + sorted, so an order-sensitive sequence compare is exact).
        referenceAreas.Should().Equal(requestAreas);
    }
}
