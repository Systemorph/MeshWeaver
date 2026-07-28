using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the NodeType COMPILE ORDER — dependencies before dependents, computed from the declared
/// sources.
///
/// <para><b>The incident.</b> A NodeType can compile another type's Code into its own assembly
/// (<c>Store/Plugin</c> declares <c>shared=@Store/Coupon/Source</c>, <c>@Store/Order/Source</c>,
/// <c>@Store/BillingProfile/Source</c>), and every plugin ROOT — <c>AgenticEngineering</c>,
/// <c>DataModeling</c> — is an INSTANCE of such a type. Nothing ordered the compiles, so a cold pod
/// raced them all and the dependents blew the 60s activation budget:</para>
///
/// <code>
/// [STALE-CALLBACK] cache/krhs…: 3 callback(s) pending &gt; 30000ms:
///     SubscribeRequest@AgenticEngineering(33034ms), SubscribeRequest@DataModeling(45043ms),
///     SubscribeRequest@Store/Plugin(45028ms)
/// System.TimeoutException: No response received … within 00:01:00 → target Store/Plugin.
/// </code>
///
/// <para>These are pure functions over paths and query strings, so the ordering rules are pinned
/// without a hub, a mesh or a compile.</para>
/// </summary>
public class NodeTypeDependencyGraphTest
{
    /// <summary>The real Store shape that produced the outage, straight from the live node.</summary>
    private static Dictionary<string, NodeTypeDefinition?> StoreShape() => new()
    {
        ["Store/Plugin"] = new NodeTypeDefinition
        {
            Sources =
            [
                "namespace:Source scope:subtree",
                "shared=@Store/Coupon/Source",
                "shared=@Store/Order/Source",
                "shared=@Store/BillingProfile/Source",
            ],
        },
        ["Store/Coupon"] = new NodeTypeDefinition(),
        ["Store/Order"] = new NodeTypeDefinition(),
        ["Store/BillingProfile"] = new NodeTypeDefinition(),
    };

    [Fact]
    public void DependenciesOf_ReadsCrossTypeSharedSources()
    {
        var types = StoreShape();

        var deps = NodeTypeDependencyGraph.DependenciesOf(
            types["Store/Plugin"], "Store/Plugin", types.Keys);

        deps.OrderBy(d => d).Should().Equal(
            new[] { "Store/BillingProfile", "Store/Coupon", "Store/Order" },
            "those three are compiled INTO Store/Plugin's assembly, so it cannot be built before them");
    }

    [Fact]
    public void DependenciesOf_OwnSubtreeIsNotADependency()
    {
        var types = StoreShape();

        var deps = NodeTypeDependencyGraph.DependenciesOf(
            types["Store/Coupon"], "Store/Coupon", types.Keys);

        deps.Should().BeEmpty(
            "a type's own Source/Test folder is not a dependency — otherwise every type would "
            + "depend on itself and nothing would ever be orderable");
    }

    [Fact]
    public void TopologicalOrder_PutsDependenciesBeforeDependents()
    {
        var order = NodeTypeDependencyGraph.TopologicalOrder(
            NodeTypeDependencyGraph.Build(StoreShape()), out var cyclic);

        cyclic.Should().BeEmpty();
        order.Should().HaveCount(4);
        foreach (var dependency in new[] { "Store/Coupon", "Store/Order", "Store/BillingProfile" })
            order.IndexOf(dependency).Should().BeLessThan(order.IndexOf("Store/Plugin"),
                $"{dependency} supplies source that is compiled into Store/Plugin");
    }

    [Fact]
    public void TopologicalOrder_IsDeterministic()
    {
        var deps = NodeTypeDependencyGraph.Build(StoreShape());

        var first = NodeTypeDependencyGraph.TopologicalOrder(deps);
        var second = NodeTypeDependencyGraph.TopologicalOrder(deps);

        second.Should().Equal(first,
            "the same mesh must warm in the same sequence every boot, or a failure is not reproducible");
    }

    [Fact]
    public void TopologicalOrder_HandlesAChain()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition { Sources = ["shared=@B/Source"] },
            ["B"] = new NodeTypeDefinition { Sources = ["shared=@C/Source"] },
            ["C"] = new NodeTypeDefinition(),
        };

        var order = NodeTypeDependencyGraph.TopologicalOrder(NodeTypeDependencyGraph.Build(types));

        order.Should().Equal(["C", "B", "A"], "a transitive chain must be fully ordered, not just one hop");
    }

    /// <summary>
    /// A cycle cannot be ordered, but it must never DROP a type — a type missing from the warm
    /// order is a type nobody compiles until a user trips over it, which is the original bug.
    /// </summary>
    [Fact]
    public void TopologicalOrder_CycleIsReportedAndStillEmitsEveryType()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["X"] = new NodeTypeDefinition { Sources = ["shared=@Y/Source"] },
            ["Y"] = new NodeTypeDefinition { Sources = ["shared=@X/Source"] },
            ["Z"] = new NodeTypeDefinition(),
        };

        var order = NodeTypeDependencyGraph.TopologicalOrder(
            NodeTypeDependencyGraph.Build(types), out var cyclic);

        cyclic.Should().Equal(["X", "Y"]);
        order.OrderBy(p => p).Should().Equal(new[] { "X", "Y", "Z" });
        order.Should().HaveCount(3, "no duplicates");
        order.IndexOf("Z").Should().Be(0, "the orderable types still go first");
    }

    [Fact]
    public void OwningType_LongestPathWins()
    {
        string[] known = ["Store", "Store/Coupon"];

        NodeTypeDependencyGraph.OwningType("Store/Coupon/Source", known)
            .Should().Be("Store/Coupon",
                "a nested type owns its own subtree — attributing it to the shorter parent path "
                + "would invent an edge to the wrong type");
    }

    [Fact]
    public void DependenciesOf_UnknownReferenceIsIgnored()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition { Sources = ["shared=@Nowhere/Source"] },
        };

        var deps = NodeTypeDependencyGraph.DependenciesOf(types["A"], "A", types.Keys);

        deps.Should().BeEmpty("an unresolvable reference must not invent an edge that stalls the order");
        NodeTypeDependencyGraph.TopologicalOrder(NodeTypeDependencyGraph.Build(types))
            .Should().Equal(["A"]);
    }

    /// <summary>
    /// The dependency edge must be read from the SAME expansion the compiler runs, or the order
    /// would be computed from a different set of files than the ones actually compiled.
    /// </summary>
    [Fact]
    public void ReferencedPaths_UsesCodeQueryResolverExpansion()
    {
        var definition = new NodeTypeDefinition { Sources = ["$self/Extra", "shared=@Other/Source"] };

        var paths = NodeTypeDependencyGraph.ReferencedPaths(definition, "Acme/Type");

        paths.Should().Contain("Other/Source");
        paths.Should().Contain(p => p.StartsWith("Acme/Type"), "$self must expand to the owning type");
    }

    /// <summary>
    /// A dependent of a BROKEN type cannot build — its assembly would be missing exactly the
    /// sources the upstream owns — so the block must be detected before the attempt, not after a
    /// per-type timeout.
    /// </summary>
    [Fact]
    public void FirstBlockedBy_NamesTheBrokenUpstream()
    {
        var deps = NodeTypeDependencyGraph.Build(StoreShape());
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Store/Coupon" };

        NodeTypeDependencyGraph.FirstBlockedBy("Store/Plugin", deps, failed)
            .Should().Be("Store/Coupon");
    }

    [Fact]
    public void FirstBlockedBy_HealthyUpstreamDoesNotBlock()
    {
        var deps = NodeTypeDependencyGraph.Build(StoreShape());
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        NodeTypeDependencyGraph.FirstBlockedBy("Store/Plugin", deps, failed).Should().BeNull();
        NodeTypeDependencyGraph.FirstBlockedBy("Store/Coupon", deps, failed).Should().BeNull(
            "a type with no dependencies can never be blocked");
    }

    /// <summary>
    /// THE PROPAGATION RULE. Walking in topological order and adding each blocked type to the
    /// failed set makes the block transitive with only DIRECT edges examined: in A→B→C, a broken C
    /// must stop B and then A. This is the exact loop <c>DynamicTypePreWarmer</c> runs, so it pins
    /// the contract the warmer depends on.
    /// </summary>
    [Fact]
    public void FirstBlockedBy_PropagatesTransitively_AlongTopologicalOrder()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition { Sources = ["shared=@B/Source"] },
            ["B"] = new NodeTypeDefinition { Sources = ["shared=@C/Source"] },
            ["C"] = new NodeTypeDefinition(),
            ["Unrelated"] = new NodeTypeDefinition(),
        };
        var deps = NodeTypeDependencyGraph.Build(types);
        var order = NodeTypeDependencyGraph.TopologicalOrder(deps);

        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<(string Type, string Blocker)>();
        var attempted = new List<string>();

        foreach (var path in order)
        {
            var blocker = NodeTypeDependencyGraph.FirstBlockedBy(path, deps, failed);
            if (blocker is not null)
            {
                skipped.Add((path, blocker));
                failed.Add(path);                 // a skipped type blocks ITS dependents too
                continue;
            }
            attempted.Add(path);
            if (path == "C") failed.Add(path);    // C is the broken one
        }

        attempted.Should().Equal(["C", "Unrelated"],
            "only C itself is attempted and fails; an UNRELATED type must still be warmed — "
            + "a broken type contains its blast radius to its own dependents");
        skipped.Should().Equal([("B", "C"), ("A", "B")],
            "B is blocked by the broken C, and A by the now-unbuildable B — transitively, "
            + "with only direct edges consulted");
        (attempted.Count + skipped.Count).Should().Be(order.Count,
            "every type is accounted for exactly once — a type silently dropped is a type nobody "
            + "compiles until a user trips over it");
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    //  Gap hunt — the cases where a plausible implementation is silently WRONG rather than
    //  obviously broken. Each of these would ship green without a test.
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The nastiest false edge available: <c>Store/CouponX</c> starts with <c>Store/Coupon</c>
    /// as a STRING but is a different type. A prefix test written as a bare
    /// <c>StartsWith(root)</c> attributes it to the wrong owner and invents a dependency that can
    /// fabricate a cycle — which would push both types into the unorderable bucket.
    /// </summary>
    [Fact]
    public void OwningType_PrefixThatIsNotAPathSegmentIsNotOwned()
    {
        string[] known = ["Store/Coupon", "Store/CouponExtra"];

        NodeTypeDependencyGraph.OwningType("Store/CouponExtra/Source", known)
            .Should().Be("Store/CouponExtra");
        NodeTypeDependencyGraph.OwningType("Store/Couponade/Source", known)
            .Should().BeNull("a shared string prefix is not a path segment boundary");
    }

    /// <summary>
    /// A type naming its OWN path explicitly (rather than via the bare-namespace default) must not
    /// produce a self-edge: a self-edge is a 1-cycle, so the type would never become "ready" and
    /// would be dumped into the cyclic bucket — unordered, for no reason.
    /// </summary>
    [Fact]
    public void DependenciesOf_ExplicitSelfReferenceIsNotASelfEdge()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["Store/Plugin"] = new NodeTypeDefinition { Sources = ["@Store/Plugin/Source", "$self/More"] },
        };

        NodeTypeDependencyGraph.DependenciesOf(types["Store/Plugin"], "Store/Plugin", types.Keys)
            .Should().BeEmpty();
        NodeTypeDependencyGraph.TopologicalOrder(NodeTypeDependencyGraph.Build(types), out var cyclic)
            .Should().Equal(["Store/Plugin"]);
        cyclic.Should().BeEmpty("a self-reference must not masquerade as a cycle");
    }

    /// <summary>
    /// Tests are compiled into the SAME assembly as sources, so a cross-type <c>Tests</c> entry is
    /// just as much a build dependency. Reading only <c>Sources</c> would silently under-order.
    /// </summary>
    [Fact]
    public void DependenciesOf_TestsEntriesAlsoCreateEdges()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition { Tests = ["shared=@B/Test"] },
            ["B"] = new NodeTypeDefinition(),
        };

        NodeTypeDependencyGraph.DependenciesOf(types["A"], "A", types.Keys)
            .Should().Equal(["B"]);
        NodeTypeDependencyGraph.TopologicalOrder(NodeTypeDependencyGraph.Build(types))
            .Should().Equal(["B", "A"]);
    }

    /// <summary>
    /// The graph compares paths case-INsensitively throughout. If the edge lookup and the topo sort
    /// disagreed on casing, a real dependency would be dropped from the order while still being
    /// reported as a dependency — the worst kind of half-consistency.
    /// </summary>
    [Fact]
    public void Ordering_IsCaseInsensitiveEndToEnd()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["Store/Plugin"] = new NodeTypeDefinition { Sources = ["shared=@store/COUPON/Source"] },
            ["Store/Coupon"] = new NodeTypeDefinition(),
        };

        var deps = NodeTypeDependencyGraph.Build(types);

        deps["Store/Plugin"].Should().Equal(["Store/Coupon"]);
        NodeTypeDependencyGraph.TopologicalOrder(deps).Should().Equal(["Store/Coupon", "Store/Plugin"]);
    }

    /// <summary>
    /// A diamond (A→B, A→C, B→D, C→D) is where a naive "one pass over the list" ordering breaks:
    /// D must precede BOTH middles, and A must come last.
    /// </summary>
    [Fact]
    public void TopologicalOrder_HandlesADiamond()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition { Sources = ["shared=@B/Source", "shared=@C/Source"] },
            ["B"] = new NodeTypeDefinition { Sources = ["shared=@D/Source"] },
            ["C"] = new NodeTypeDefinition { Sources = ["shared=@D/Source"] },
            ["D"] = new NodeTypeDefinition(),
        };

        var order = NodeTypeDependencyGraph.TopologicalOrder(NodeTypeDependencyGraph.Build(types));

        order.Should().Equal(["D", "B", "C", "A"]);
    }

    /// <summary>
    /// Determinism must survive a different DICTIONARY INSERTION ORDER, not just a second call on
    /// the same instance — hash order is the thing that actually varies between processes, and the
    /// earlier determinism test could not have caught a dependence on it.
    /// </summary>
    [Fact]
    public void TopologicalOrder_IsIndependentOfInsertionOrder()
    {
        var forward = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition { Sources = ["shared=@B/Source"] },
            ["B"] = new NodeTypeDefinition { Sources = ["shared=@C/Source"] },
            ["C"] = new NodeTypeDefinition(),
            ["Solo"] = new NodeTypeDefinition(),
        };
        var reversed = new Dictionary<string, NodeTypeDefinition?>
        {
            ["Solo"] = new NodeTypeDefinition(),
            ["C"] = new NodeTypeDefinition(),
            ["B"] = new NodeTypeDefinition { Sources = ["shared=@C/Source"] },
            ["A"] = new NodeTypeDefinition { Sources = ["shared=@B/Source"] },
        };

        NodeTypeDependencyGraph.TopologicalOrder(NodeTypeDependencyGraph.Build(reversed))
            .Should().Equal(NodeTypeDependencyGraph.TopologicalOrder(NodeTypeDependencyGraph.Build(forward)));
    }

    /// <summary>
    /// A type that DEPENDS ON a cycle can never be ordered either. It must still be emitted and
    /// reported — dropping it is the original bug (a type nobody compiles), and claiming it is
    /// orderable would place it before the dependency it is waiting on.
    /// </summary>
    [Fact]
    public void TopologicalOrder_DependentOfACycleIsReported_NotDropped()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["X"] = new NodeTypeDefinition { Sources = ["shared=@Y/Source"] },
            ["Y"] = new NodeTypeDefinition { Sources = ["shared=@X/Source"] },
            ["W"] = new NodeTypeDefinition { Sources = ["shared=@X/Source"] },
            ["Free"] = new NodeTypeDefinition(),
        };

        var order = NodeTypeDependencyGraph.TopologicalOrder(
            NodeTypeDependencyGraph.Build(types), out var cyclic);

        order.Should().HaveCount(4);
        order.IndexOf("Free").Should().Be(0);
        cyclic.Should().Equal(["W", "X", "Y"],
            "W is not itself in the cycle but is downstream of one, so it is equally unorderable "
            + "— reporting it is honest; silently ordering it would be a lie");
    }

    /// <summary>A null definition falls back to the DEFAULT own-subtree queries — never a crash,
    /// never an edge.</summary>
    [Fact]
    public void DependenciesOf_NullDefinitionUsesDefaultsAndHasNoEdges()
    {
        string[] known = ["A", "B"];

        NodeTypeDependencyGraph.DependenciesOf(null, "A", known).Should().BeEmpty();
        NodeTypeDependencyGraph.ReferencedPaths(null, "A")
            .Should().Contain(p => p.StartsWith("A/"), "the defaults resolve to the type's own folders");
    }

    /// <summary>Blank entries are skipped, not turned into a wildcard edge or an exception.</summary>
    [Fact]
    public void DependenciesOf_BlankSourceEntriesAreIgnored()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition { Sources = ["", "   ", "shared=@B/Source"] },
            ["B"] = new NodeTypeDefinition(),
        };

        NodeTypeDependencyGraph.DependenciesOf(types["A"], "A", types.Keys).Should().Equal(["B"]);
    }

    /// <summary>With several broken upstreams the reported blocker must be stable, or the same
    /// outage reads differently on every boot.</summary>
    [Fact]
    public void FirstBlockedBy_IsDeterministicAcrossMultipleFailures()
    {
        var deps = NodeTypeDependencyGraph.Build(StoreShape());
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Store/Order", "Store/Coupon", "Store/BillingProfile" };

        NodeTypeDependencyGraph.FirstBlockedBy("Store/Plugin", deps, failed)
            .Should().Be("Store/BillingProfile", "path order, so the message never flaps");
    }

    /// <summary>An unknown path is not blocked by anything — it has no entry in the map at all.</summary>
    [Fact]
    public void FirstBlockedBy_UnknownTypeIsNotBlocked()
    {
        var deps = NodeTypeDependencyGraph.Build(StoreShape());
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Store/Coupon" };

        NodeTypeDependencyGraph.FirstBlockedBy("Not/AType", deps, failed).Should().BeNull();
    }

    [Fact]
    public void TopologicalOrder_EmptyMeshIsEmpty()
    {
        var order = NodeTypeDependencyGraph.TopologicalOrder(
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty, out var cyclic);

        order.Should().BeEmpty();
        cyclic.Should().BeEmpty();
    }
}
