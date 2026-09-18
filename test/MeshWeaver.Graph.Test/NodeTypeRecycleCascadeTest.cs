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
            + "is still recycled, and its instances are recycled by hand if that leg had failed");
    }
}
