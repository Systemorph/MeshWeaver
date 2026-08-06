#pragma warning disable CS1591

using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure tests for <see cref="AgentPickerProjection.BuildSkillSubtreeQueries"/> — the layered
/// skill-discovery queries <c>MeshAgentSkillsSource</c> resolves an agent round's skills through.
/// No mesh required.
///
/// <para>The contract under test is the platform's established layering: <b>1.</b> the user's own,
/// <b>2.</b> the context node's partition, <b>3.</b> the node type's partition, then the platform
/// defaults — with layers 2 and 3 widened to the partition SUBTREE so a space or plugin can author a
/// skill next to the content it belongs to.</para>
/// </summary>
public class AgentSkillSubtreeQueriesTest
{
    private const string PlatformDefaults = "namespace:Skill nodeType:Skill";

    [Fact]
    public void AllThreeLayers_AreOrderedUserThenSpaceThenNodeType_WithPlatformLast()
    {
        var queries = AgentPickerProjection.BuildSkillSubtreeQueries(
            userPath: "rbuergi",
            spacePath: "AgenticPension/Portfolios/Fund",
            nodeTypePath: "Office/Slide");

        queries.Should().Equal(
            "namespace:rbuergi/Skill nodeType:Skill",
            "path:AgenticPension scope:descendants nodeType:Skill",
            "path:Office scope:descendants nodeType:Skill",
            PlatformDefaults);
    }

    [Fact]
    public void UserLayer_StaysAFlatNamespace_NotASubtree()
    {
        // A user's own skills are placed by convention in {user}/Skill; widening that layer to the
        // whole user partition would sweep in every node the user owns.
        AgentPickerProjection.BuildSkillSubtreeQueries(userPath: "rbuergi")
            .Should().Equal("namespace:rbuergi/Skill nodeType:Skill", PlatformDefaults);
    }

    [Fact]
    public void SpaceAndNodeTypeLayers_ScopeToTheirPartitionSubtree_NotJustTheSkillNamespace()
    {
        // The whole point: a skill authored deep inside the space (e.g. AgenticPension/Reports/Skill/x)
        // must be found, which a flat namespace:{partition}/Skill membership filter would miss.
        AgentPickerProjection.BuildSkillSubtreeQueries(spacePath: "AgenticPension/Reports/Q3")
            .Should().Equal("path:AgenticPension scope:descendants nodeType:Skill", PlatformDefaults);
    }

    [Fact]
    public void SamePartitionInTwoLayers_IsListedOnce()
    {
        // A node whose type ships in its own space contributes the same subtree twice; one query is
        // enough and the union would otherwise pay for the duplicate.
        AgentPickerProjection.BuildSkillSubtreeQueries(
                spacePath: "Office/Decks/Deck1", nodeTypePath: "Office/Slide")
            .Should().Equal("path:Office scope:descendants nodeType:Skill", PlatformDefaults);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("welcome")]
    [InlineData("settings")]
    public void ReservedRoutePartitions_AreDropped(string reserved)
    {
        // Rogue auto-minted route partitions carry no read policy; including one fails the WHOLE
        // query and empties skill discovery for the round.
        AgentPickerProjection.BuildSkillSubtreeQueries(spacePath: $"{reserved}/Page")
            .Should().Equal(PlatformDefaults);
    }

    [Fact]
    public void NoContextAtAll_StillServesThePlatformDefaults()
    {
        AgentPickerProjection.BuildSkillSubtreeQueries().Should().Equal(PlatformDefaults);
    }

    [Fact]
    public void PlatformDefaults_AreAlwaysLast()
    {
        // Precedence is "most specific first"; the platform layer is the fallback, so it must never
        // shadow a user's or a space's override of the same skill name.
        AgentPickerProjection
            .BuildSkillSubtreeQueries("rbuergi", "AgenticPension", "Office/Slide")
            .Last().Should().Be(PlatformDefaults);
    }
}
