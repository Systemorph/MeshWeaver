using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Moved-node redirects: a <c>Redirect</c> node (content <see cref="NodeRedirect"/>) left at a
/// retired path sends navigation to the new location, subtree and all.
///
/// <para>Every declaration is seeded STATICALLY in <see cref="ConfigureMesh"/> rather than created
/// per test. Redirect resolution runs through the same catalog query the router uses, so a
/// create-then-resolve test would be racing change-feed propagation and would prove timing rather
/// than behaviour.</para>
/// </summary>
public class NodeRedirectResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — every case reads the same static seed.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private const string New = $"{TestPartition}/New";
    private const string Old = $"{TestPartition}/Old";
    private const string ExactOld = $"{TestPartition}/ExactOld";
    private const string ChainA = $"{TestPartition}/ChainA";
    private const string ChainB = $"{TestPartition}/ChainB";
    private const string LoopA = $"{TestPartition}/LoopA";
    private const string LoopB = $"{TestPartition}/LoopB";
    private const string SelfLoop = $"{TestPartition}/SelfLoop";
    private const string Untargeted = $"{TestPartition}/Untargeted";
    private const string ToNowhere = $"{TestPartition}/ToNowhere";
    private const string NowhereTarget = $"{TestPartition}/NotThere/Deep";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                // The destination subtree the retired paths point at.
                Page(New, "New Home"),
                Page($"{New}/A", "A"),
                Page($"{New}/A/B", "B"),

                // The driving case: one declaration, whole subtree.
                Redirect(Old, New),
                // Same target, but the declaration covers only its own path.
                Redirect(ExactOld, New, RedirectScope.Exact),
                // A → B → destination, inside the hop cap.
                Redirect(ChainA, ChainB),
                Redirect(ChainB, New),
                // A → B → A, and the degenerate A → A.
                Redirect(LoopA, LoopB),
                Redirect(LoopB, LoopA),
                Redirect(SelfLoop, SelfLoop),
                // Inert declaration: no destination was ever set.
                Redirect(Untargeted, null),
                // Points at a path where no node exists.
                Redirect(ToNowhere, NowhereTarget));

    private static MeshNode Page(string path, string name) => MeshNode.FromPath(path) with
    {
        Name = name, NodeType = "Markdown", State = MeshNodeState.Active
    };

    private static MeshNode Redirect(string path, string? target, RedirectScope scope = RedirectScope.Subtree)
        => MeshNode.FromPath(path) with
        {
            Name = path,
            NodeType = NodeRedirectRules.NodeTypeName,
            State = MeshNodeState.Active,
            Content = new NodeRedirect { TargetPath = target, Scope = scope }
        };

    private IPathResolver Resolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

    private Task<AddressResolution?> ResolveNav(string path) =>
        Resolver.ResolveNavigationPath(path).FirstAsync().Timeout(TimeSpan.FromSeconds(20))
            .ToTask(TestContext.Current.CancellationToken);

    private Task<AddressResolution?> ResolveLiteral(string path) =>
        Resolver.ResolvePath(path).FirstAsync().Timeout(TimeSpan.FromSeconds(20))
            .ToTask(TestContext.Current.CancellationToken);

    /// <summary>The full path a resolution stands for — prefix with the unmatched remainder re-attached.</summary>
    private static string Full(AddressResolution r) =>
        string.IsNullOrEmpty(r.Remainder) ? r.Prefix : $"{r.Prefix}/{r.Remainder}";

    // ── the driving case ────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Subtree_declaration_moves_a_deep_link_not_just_the_root()
    {
        var resolved = await ResolveNav($"{Old}/A/B");

        resolved.Should().NotBeNull();
        resolved!.Prefix.Should().Be($"{New}/A/B",
            "ONE declaration at the retired root must carry the whole subtree — a root-only redirect "
            + "would leave every deep bookmark and every markdown link into the retired module dead, "
            + "which is the entire reason the mechanism exists");
        resolved.Remainder.Should().BeNullOrEmpty();
        resolved.RedirectedFrom.Should().Be($"{Old}/A/B",
            "the GUI needs the path the viewer actually typed, both to rewrite the URL to the "
            + "canonical target and to tell them they were moved");
        resolved.RedirectDiagnostic.Should().BeNull();
    }

    [Fact(Timeout = 30000)]
    public async Task Exact_path_declaration_resolves_the_declaring_path_itself()
    {
        var resolved = await ResolveNav(Old);

        resolved!.Prefix.Should().Be(New);
        resolved.RedirectedFrom.Should().Be(Old);
    }

    [Fact(Timeout = 30000)]
    public async Task Exact_scope_covers_only_its_own_path_and_leaves_deep_links_alone()
    {
        (await ResolveNav(ExactOld))!.Prefix.Should().Be(New,
            "an Exact declaration still redirects the path it is declared on");

        var deep = await ResolveNav($"{ExactOld}/A");
        deep!.Prefix.Should().Be(ExactOld,
            "Exact means exact — a deep link falls through to the redirect node, whose view names "
            + "the destination, rather than being sent to a guessed location under the new root");
        deep.Remainder.Should().Be("A");
        deep.RedirectedFrom.Should().BeNull();
    }

    [Fact(Timeout = 30000)]
    public async Task A_chain_within_the_hop_cap_lands_on_the_final_target()
    {
        var resolved = await ResolveNav($"{ChainA}/A");

        resolved!.Prefix.Should().Be($"{New}/A",
            "A → B → destination must collapse to the destination in ONE resolution, so the GUI "
            + "performs a single navigation instead of bouncing the browser through each hop");
        resolved.RedirectedFrom.Should().Be($"{ChainA}/A",
            "the ORIGINAL request is what the viewer is told about — the intermediate hop is an "
            + "implementation detail nobody typed");
        resolved.RedirectDiagnostic.Should().BeNull();
    }

    // ── termination ─────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task A_cycle_fails_loudly_instead_of_hanging()
    {
        var stopwatch = Stopwatch.StartNew();
        var resolved = await ResolveNav(LoopA);
        stopwatch.Stop();

        resolved.Should().NotBeNull("a cycle must produce an ANSWER — the failure mode to avoid is "
            + "a resolution that never emits, which parks the navigation and, on a hub, wedges it");
        resolved!.RedirectDiagnostic.Should().Be(MeshWeaver.Mesh.RedirectDiagnostic.Loop,
            "the reason must be a VALUE the GUI and this test can read, not only a log line");
        resolved.RedirectedFrom.Should().BeNull(
            "a chain that never arrived must not report a successful redirect — that would send the "
            + "browser somewhere on the strength of a broken declaration");
        Full(resolved).Should().BeOneOf(LoopA, LoopB,
            "the viewer lands on a redirect node, whose view names where it was trying to go");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
            "cycle detection is what bounds the walk; without it this call never returns");
    }

    [Fact(Timeout = 30000)]
    public async Task A_self_redirect_is_a_cycle_too()
    {
        var resolved = await ResolveNav(SelfLoop);

        resolved!.RedirectDiagnostic.Should().Be(MeshWeaver.Mesh.RedirectDiagnostic.Loop);
        resolved.Prefix.Should().Be(SelfLoop);
    }

    [Fact(Timeout = 30000)]
    public async Task A_declaration_with_no_destination_is_reported_not_silently_followed()
    {
        var resolved = await ResolveNav(Untargeted);

        resolved!.RedirectDiagnostic.Should().Be(MeshWeaver.Mesh.RedirectDiagnostic.TargetMissing);
        resolved.Prefix.Should().Be(Untargeted);
        resolved.RedirectedFrom.Should().BeNull();
    }

    [Fact(Timeout = 30000)]
    public async Task A_destination_that_does_not_exist_still_lands_the_viewer_on_that_path()
    {
        var resolved = await ResolveNav(ToNowhere);

        // The redirect IS followed — the resolver deliberately does not require the destination to
        // be a node, because a perfectly good destination can be a layout AREA of one
        // (".../Underwriting/Overview"). What it must not do is pretend the old path is fine.
        resolved!.RedirectedFrom.Should().Be(ToNowhere);
        Full(resolved).Should().Be(NowhereTarget);

        await ReadNode(NowhereTarget).Should().Within(ReadNodeTimeout).Match(n => n is null,
            "nothing exists at the destination — the navigation layer's nearest-existing-ancestor "
            + "fallback is what turns that into a useful page, and it needs the viewer to be AT the "
            + "destination path for its answer to be about the destination");
    }

    // ── which surfaces follow, and which deliberately do not ────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Message_routing_and_node_reads_stay_literal()
    {
        var literal = await ResolveLiteral($"{Old}/A/B");

        literal!.Prefix.Should().Be(Old,
            "ResolvePath is what message routing and single-node reads go through. If it followed "
            + "redirects, a read of the old path would answer with a DIFFERENT node than the caller "
            + "named and a write would land somewhere nobody addressed — the exact corruption the "
            + "'NO FALLBACK' rule in RoutingServiceBase.RouteMessage exists to prevent");
        literal.Remainder.Should().Be("A/B");
        literal.RedirectedFrom.Should().BeNull();

        await ReadNode($"{Old}/A/B").Should().Within(ReadNodeTimeout).Match(n => n is null,
            "a node read of a retired deep path is honestly absent, not silently answered from the "
            + "new location");
    }
}

/// <summary>
/// The pure rules behind redirect resolution — no mesh, no hub, no I/O. Rewriting and cycle
/// detection are exactly the parts that must be right for the walk to terminate, so they are
/// asserted directly rather than only through an integration path.
/// </summary>
public class NodeRedirectRulesTest
{
    [Theory]
    [InlineData("New", "A/B", "New/A/B")]     // subtree carries the remainder
    [InlineData("New", null, "New")]          // the declaring path itself
    [InlineData("New", "", "New")]
    [InlineData("/New/", "A", "New/A")]       // leading/trailing slashes are tolerated
    public void Subtree_rewrite_reattaches_the_remainder(string target, string? remainder, string expected)
        => NodeRedirectRules
            .Rewrite(new NodeRedirect { TargetPath = target }, remainder)
            .Should().Be(expected);

    [Fact]
    public void Exact_scope_rewrites_only_when_there_is_no_remainder()
    {
        var exact = new NodeRedirect { TargetPath = "New", Scope = RedirectScope.Exact };
        NodeRedirectRules.Rewrite(exact, null).Should().Be("New");
        NodeRedirectRules.Rewrite(exact, "A").Should().BeNull("a deep link must not follow an Exact declaration");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void A_declaration_with_no_destination_never_rewrites(string? target)
        => NodeRedirectRules.Rewrite(new NodeRedirect { TargetPath = target }, "A").Should().BeNull();

    [Fact]
    public void No_declaration_never_rewrites()
        => NodeRedirectRules.Rewrite(null, "A").Should().BeNull();

    [Fact]
    public void A_cycle_is_a_revisit_of_any_path_on_the_chain()
    {
        var visited = ImmutableHashSet.Create("A", "B");
        NodeRedirectRules.IsCycle(visited, "A").Should().BeTrue("A → B → A");
        NodeRedirectRules.IsCycle(visited, "/B/").Should().BeTrue("normalization must not let a cycle slip past");
        NodeRedirectRules.IsCycle(visited, "C").Should().BeFalse();
    }

    [Fact]
    public void The_hop_cap_is_a_real_bound()
        => NodeRedirectRules.MaxHops.Should().BeGreaterThan(0).And.BeLessThan(100,
            "the cap must bound an acyclic-but-long chain, which cycle detection alone walks happily; "
            + "every hop is a live resolution query, so an unbounded walk is unbounded work on a navigation");
}
