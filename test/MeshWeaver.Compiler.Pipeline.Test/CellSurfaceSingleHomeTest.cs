using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the PURE half of the cell-surface single-home rule (issue #1649 part 3): which FOREIGN
/// NodeType owners a resolved source set reaches into. The owner of a source node is derived
/// from the path shape every source query resolves against (<c>{owner}/Source/…</c> /
/// <c>{owner}/Test/…</c>) — no catalog, no mesh, no I/O — so the rule's edge cases are pinned
/// without a fixture. The impure half (reading the owner's definition and failing the consumer's
/// compile when the owner declares <c>cellSurface: true</c>) is covered by
/// <c>CellSurfaceScriptingSeamTest</c> in Hosting.Monolith.Test.
/// </summary>
public class CellSurfaceSingleHomeTest
{
    [Fact]
    public void OwnSources_DeriveNoForeignOwners()
    {
        NodeTypeDependencyGraph.ForeignSourceOwners(
                "Edu/TrainingSim",
                ["Edu/TrainingSim/Source/Sim", "Edu/TrainingSim/Source/Sub/Helper", "Edu/TrainingSim/Test/SimTest"])
            .Should().BeEmpty("a type's own Source/Test subtree is not a foreign reach");
    }

    [Fact]
    public void SharedConsumption_DerivesTheOwningType()
    {
        var owners = NodeTypeDependencyGraph.ForeignSourceOwners(
            "Edu/Lesson",
            ["Edu/Lesson/Source/Lesson", "Edu/TrainingSim/Source/Sim"]);

        owners.Should().Equal(["Edu/TrainingSim"]);
    }

    [Fact]
    public void NestedTypeOwnership_IsExactBySegment()
    {
        // Store vs Store/Coupon — the /Source/ segment position resolves ownership exactly,
        // where prefix matching would need the whole type catalog (longest-wins).
        var owners = NodeTypeDependencyGraph.ForeignSourceOwners(
            "Store",
            ["Store/Source/Root", "Store/Coupon/Source/Coupon"]);

        owners.Should().Equal(["Store/Coupon"]);
    }

    [Fact]
    public void PathsWithoutSourceSegment_NameNoOwner()
    {
        NodeTypeDependencyGraph.ForeignSourceOwners(
                "Edu/Lesson",
                ["Edu/Materials/Handout", "Source/Loose", ""])
            .Should().BeEmpty("a path without a /Source/ or /Test/ segment names no owning NodeType");
    }

    [Fact]
    public void OwnerComparison_IsCaseInsensitive_AndDeduplicated()
    {
        var owners = NodeTypeDependencyGraph.ForeignSourceOwners(
            "edu/lesson",
            ["Edu/Lesson/Source/A", "Edu/TrainingSim/Source/B", "edu/trainingsim/Test/C"]);

        owners.Should().HaveCount(1, "the same owner in different casings is one owner, and "
            + "'edu/lesson' vs 'Edu/Lesson' is the same self");
    }

    [Fact]
    public void CellSurface_RoundTripsThroughJson()
    {
        // The flag is authored in a pack's index.json as `cellSurface: true` — it must survive
        // the camelCase round-trip and default to false when absent.
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var parsed = JsonSerializer.Deserialize<NodeTypeDefinition>(
            """{"cellSurface": true, "description": "pack type"}""", options)!;
        parsed.CellSurface.Should().BeTrue();

        JsonSerializer.Deserialize<NodeTypeDefinition>("{}", options)!
            .CellSurface.Should().BeFalse("absent means opted out");

        var reserialized = JsonSerializer.Serialize(parsed, options);
        JsonSerializer.Deserialize<NodeTypeDefinition>(reserialized, options)!
            .CellSurface.Should().BeTrue("the opt-in must survive a write-back round-trip");
    }
}
