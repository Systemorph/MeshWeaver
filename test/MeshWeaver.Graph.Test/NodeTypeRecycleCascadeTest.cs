using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The dependency network a NodeType recycle fans out to — pure, over the same definitions the
/// compile order is derived from: transitive dependents (the reverse of
/// <see cref="NodeTypeDependencyGraph.Build"/>), every instance of the type and of each dependent,
/// a deterministic order, and a cycle that is ONE wave.
/// </summary>
public class NodeTypeRecycleCascadeTest
{
    private static NodeTypeDefinition Sharing(params string[] shared) =>
        new() { Sources = shared.Select(s => $"shared=@{s}/Source").ToImmutableList() };

    private static readonly IReadOnlyDictionary<string, NodeTypeDefinition?> Types =
        new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Store/Core"] = new(),
            ["Store/Licensing"] = Sharing("Store/Core"),
            ["Store/Order"] = Sharing("Store/Core", "Store/Licensing"),
            ["Store/Catalog"] = Sharing("Store/Order"),
            ["Edu/Course"] = new(),
            ["Edu/Lesson"] = Sharing("Edu/Course"),
        };

    [Fact]
    public void Dependents_AreTransitive_AndExcludeTheTypeItself()
    {
        var dependents = NodeTypeRecycleCascade.DependentsOf(Types, "Store/Core");

        dependents.Should().Equal("Store/Catalog", "Store/Licensing", "Store/Order");
    }

    [Fact]
    public void Dependents_AreOnlyThoseThatReachTheType()
    {
        NodeTypeRecycleCascade.DependentsOf(Types, "Store/Order").Should().Equal("Store/Catalog");
        NodeTypeRecycleCascade.DependentsOf(Types, "Store/Catalog").Should().BeEmpty(
            "a leaf has no dependents — its recycle reaches its own instances and nothing else");
        NodeTypeRecycleCascade.DependentsOf(Types, "Edu/Course").Should().Equal(["Edu/Lesson"],
            "an unrelated tree is not dragged in by a recycle of another");
    }

    [Fact]
    public void ACycle_IsOneWave_NotAStorm()
    {
        var cyclic = new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Sharing("B"),
            ["B"] = Sharing("A"),
            ["C"] = Sharing("A"),
        };

        NodeTypeRecycleCascade.DependentsOf(cyclic, "A").Should().Equal(["B", "C"],
            "each type appears once: the network is a SET derived at the main node, and every "
            + "fanned-out request carries CascadedFrom so it never fans out again");
    }

    [Fact]
    public void NetworkAddresses_AreDependentTypeHubs_ThenEveryInstance_OnceEach()
    {
        var dependents = NodeTypeRecycleCascade.DependentsOf(Types, "Store/Core");
        var instances = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Store/Core"] = ["Admin/Store"],
            ["Store/Licensing"] = ["Store/Licences/Pro", "Store/Licences/Free"],
            ["Store/Order"] = ["rbuergi/_Orders/1", "Store/Licences/Pro" /* a duplicate by path */],
            ["Store/Catalog"] = [],
        };

        var network = NodeTypeRecycleCascade.NetworkAddresses("Store/Core", dependents, instances);

        network.Should().Equal(
            "Store/Catalog", "Store/Licensing", "Store/Order",
            "Admin/Store",
            "Store/Licences/Pro", "Store/Licences/Free",
            "rbuergi/_Orders/1");
        network.Should().NotContain("Store/Core",
            "the type being recycled is the request that started this — it is not in its own network");
    }

    [Fact]
    public void AnInstanceMissingFromTheIndex_IsSimplyNotInTheNetwork()
    {
        var network = NodeTypeRecycleCascade.NetworkAddresses(
            "Edu/Course",
            NodeTypeRecycleCascade.DependentsOf(Types, "Edu/Course"),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

        network.Should().Equal(["Edu/Lesson"],
            "an enumeration leg that answered nothing contributes nothing — the dependent's own hub "
            + "is still recycled, and a type with no instances is a COMPLETE answer");
    }

    private static EnumerationLeg Answered(string type, params string[] instances) =>
        new(type, instances.ToImmutableList());

    private static EnumerationLeg Failed(string type, string why) =>
        new(type, ImmutableList<string>.Empty, why);

    /// <summary>
    /// 🚨 <b>The control for "an error must never read like a clean pass".</b> Two composes over
    /// legs that produce the SAME address list: one where a type genuinely has no instances, one
    /// where its enumeration failed. Before the failure was carried through, both produced the
    /// identical answer and the cascade reported success over hubs that stayed on the old assembly.
    /// The addresses still match — that is the point — and only <c>IsComplete</c> separates them.
    /// </summary>
    [Fact]
    public void AFailedLeg_AndAnEmptyOne_ProduceTheSameAddresses_AndOppositeVerdicts()
    {
        var dependents = NodeTypeRecycleCascade.DependentsOf(Types, "Store/Core");

        var clean = NodeTypeRecycleCascade.Compose("Store/Core", dependents, null,
        [
            Answered("Store/Core", "Admin/Store"),
            Answered("Store/Licensing"),
            Answered("Store/Order", "rbuergi/_Orders/1"),
            Answered("Store/Catalog"),
        ]);

        var lost = NodeTypeRecycleCascade.Compose("Store/Core", dependents, null,
        [
            Answered("Store/Core", "Admin/Store"),
            Failed("Store/Licensing", "TimeoutException: The operation has timed out."),
            Answered("Store/Order", "rbuergi/_Orders/1"),
            Answered("Store/Catalog"),
        ]);

        lost.Addresses.Should().Equal(clean.Addresses,
            "a failed leg contributes no addresses — exactly like an empty one, which is why the "
            + "address list ALONE can never tell the two apart");

        clean.IsComplete.Should().BeTrue("every leg answered");
        lost.IsComplete.Should().BeFalse(
            "one leg did not, so an unknown number of Store/Licensing instance hubs keep serving "
            + "the assembly they were born with — the cascade must not report this as a clean pass");
        lost.Incomplete.Should().ContainSingle()
            .Which.Should().Contain("Store/Licensing").And.Contain("TimeoutException",
                "and it must name WHICH leg was lost and why, or nobody can recycle the remainder by hand");
    }

    /// <summary>
    /// A failed leg does not stop the cascade fanning out to the addresses that WERE derived: those
    /// activations are stale whatever happened elsewhere, and a partial recycle beats none. The
    /// dependent's own TYPE hub is among them — it is its INSTANCE leg that failed.
    /// </summary>
    [Fact]
    public void AFailedLeg_StillLeavesEveryDerivedAddressInTheNetwork()
    {
        var dependents = NodeTypeRecycleCascade.DependentsOf(Types, "Store/Core");

        var result = NodeTypeRecycleCascade.Compose("Store/Core", dependents, null,
        [
            Answered("Store/Core", "Admin/Store"),
            Failed("Store/Licensing", "TimeoutException: The operation has timed out."),
            Answered("Store/Order", "rbuergi/_Orders/1"),
            Answered("Store/Catalog"),
        ]);

        result.Addresses.Should().Contain("Store/Licensing",
            "the dependent TYPE's own hub comes from the dependency walk, not from the instance leg");
        result.Addresses.Should().Contain(["Admin/Store", "rbuergi/_Orders/1"],
            "and every instance another leg did answer is still recycled");
    }

    /// <summary>
    /// When the NodeType enumeration itself fails, the dependents are UNKNOWN — not "none". The
    /// type's own instances are still recycled, and the answer says which half is missing.
    /// </summary>
    [Fact]
    public void AFailedTypesLeg_SaysTheDependentsAreUnknown_AndStillRecyclesTheOwnInstances()
    {
        var result = NodeTypeRecycleCascade.Compose(
            "Store/Core",
            ImmutableList<string>.Empty,
            "TimeoutException: The operation has timed out.",
            [Answered("Store/Core", "Admin/Store")]);

        result.Addresses.Should().Equal(["Admin/Store"],
            "with no dependents derivable, the type's own instances are all that can be reached");
        result.IsComplete.Should().BeFalse(
            "an empty dependent list from a FAILED walk is not the same answer as a leaf type's "
            + "genuinely empty one");
        result.Incomplete.Should().ContainSingle()
            .Which.Should().Contain("DEPENDENTS").And.Contain("TimeoutException");
    }

    /// <summary>
    /// The positive control for the three above: a fully answered compose is COMPLETE and says
    /// nothing is missing. Without this, reporting every compose as incomplete would pass them all.
    /// </summary>
    [Fact]
    public void EveryLegAnswering_IsAComplete_AndSilent_Result()
    {
        var result = NodeTypeRecycleCascade.Compose(
            "Edu/Course",
            NodeTypeRecycleCascade.DependentsOf(Types, "Edu/Course"),
            null,
            [Answered("Edu/Course", "rbuergi/Courses/Intro"), Answered("Edu/Lesson")]);

        result.IsComplete.Should().BeTrue();
        result.Incomplete.Should().BeEmpty();
        result.Addresses.Should().Equal(["Edu/Lesson", "rbuergi/Courses/Intro"]);
    }
}
