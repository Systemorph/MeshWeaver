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

    [Fact]
    public void TopologicalOrder_EmptyMeshIsEmpty()
    {
        var order = NodeTypeDependencyGraph.TopologicalOrder(
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty, out var cyclic);

        order.Should().BeEmpty();
        cyclic.Should().BeEmpty();
    }
}
