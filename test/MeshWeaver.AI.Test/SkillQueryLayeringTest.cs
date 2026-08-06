#pragma warning disable CS1591

using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure tests for <see cref="AgentPickerProjection.BuildSkillQueries"/> — the layered
/// skill-discovery queries <c>MeshAgentSkillsSource</c> resolves an agent round's skills through.
/// No mesh required.
///
/// <para>The contract under test is the platform's established layering — the platform defaults, the
/// user's own, the context node's partition and the node type's partition — with the two partition
/// layers scoped to the whole SUBTREE so a space or plugin can author a skill next to the content it
/// belongs to. Precedence between layers is resolved from each result's own partition, NOT from the
/// order of these rows.</para>
/// </summary>
public class SkillQueryLayeringTest
{
    private const string Proj = AgentPickerProjection.RegistryProjection;
    private const string PlatformDefaults = "namespace:Skill nodeType:Skill" + Proj;

    [Fact]
    public void AllLayers_AreEmitted_PlatformFirstThenUserThenSpaceThenNodeType()
    {
        var queries = AgentPickerProjection.BuildSkillQueries(
            userPath: "rbuergi",
            spacePath: "AgenticPension/Portfolios/Fund",
            nodeTypePath: "Office/Slide");

        queries.Should().Equal(
            PlatformDefaults,
            "namespace:rbuergi/Skill nodeType:Skill" + Proj,
            "path:AgenticPension scope:descendants nodeType:Skill" + Proj,
            "path:Office scope:descendants nodeType:Skill" + Proj);
    }

    [Fact]
    public void UserLayer_StaysAFlatNamespace_NotASubtree()
    {
        // A user's own skills are placed by convention in {user}/Skill; widening that layer to the
        // whole user partition would sweep in every node the user owns.
        AgentPickerProjection.BuildSkillQueries(userPath: "rbuergi")
            .Should().Equal(PlatformDefaults, "namespace:rbuergi/Skill nodeType:Skill" + Proj);
    }

    [Fact]
    public void SpaceAndNodeTypeLayers_ScopeToTheirPartitionSubtree_NotJustTheSkillNamespace()
    {
        // The whole point: a skill authored deep inside the space (e.g. AgenticPension/Reports/Skill/x)
        // must be found, which a flat namespace:{partition}/Skill membership filter would miss.
        AgentPickerProjection.BuildSkillQueries(spacePath: "AgenticPension/Reports/Q3")
            .Should().Equal(PlatformDefaults, "path:AgenticPension scope:descendants nodeType:Skill" + Proj);
    }

    [Fact]
    public void SamePartitionInTwoLayers_IsListedOnce()
    {
        // A node whose type ships in its own space contributes the same subtree twice; one query is
        // enough and the union would otherwise pay for the duplicate.
        AgentPickerProjection.BuildSkillQueries(
                spacePath: "Office/Decks/Deck1", nodeTypePath: "Office/Slide")
            .Should().Equal(PlatformDefaults, "path:Office scope:descendants nodeType:Skill" + Proj);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("welcome")]
    [InlineData("settings")]
    public void ReservedRoutePartitions_AreDropped(string reserved)
    {
        // Rogue auto-minted route partitions carry no read policy; including one fails the WHOLE
        // query and empties skill discovery for the round.
        AgentPickerProjection.BuildSkillQueries(spacePath: $"{reserved}/Page")
            .Should().Equal(PlatformDefaults);
    }

    [Fact]
    public void NoContextAtAll_StillServesThePlatformDefaults()
    {
        AgentPickerProjection.BuildSkillQueries().Should().Equal(PlatformDefaults);
    }

    // ─── The consistency guard: GUI ↔ agent framework ───
    // The chat resolves its skill sources through AiSettings templates, and MeshAgentSkillsSource
    // makes the SAME call. These tests pin the two definitions together — if they ever drift, a user
    // sees skills in the slash menu that the agent does not have (or vice versa), which is silent.

    [Theory]
    [InlineData("rbuergi", "AgenticPension/Reports/Q3", "Office/Slide")]
    [InlineData("rbuergi", "AgenticPension", null)]
    [InlineData(null, "AgenticPension", "Office/Slide")]
    [InlineData("rbuergi", null, null)]
    [InlineData(null, null, null)]
    [InlineData("rbuergi", "login/Page", "settings/Whatever")]
    [InlineData("rbuergi", "Office/Decks/Deck1", "Office/Slide")]
    public void TheSettingsDefaults_ResolveToTheSameSetAsTheCanonicalBuilder(
        string? userPath, string? contextPath, string? nodeTypePath)
    {
        AiSettingsNodeType.ResolveSkillQueries(null, contextPath, nodeTypePath, userPath)
            .Should().Equal(AgentPickerProjection.BuildSkillQueries(userPath, contextPath, nodeTypePath));
    }

    [Fact]
    public void TheSettingsDefaultTemplates_CoverExactlyTheFourLayers()
    {
        // One row per layer. A template list that loses a row silently removes a whole layer of
        // skills from both the chat and the agent framework.
        AiSettingsNodeType.DefaultSkillQueryTemplates.Should().Equal(
            "namespace:Skill nodeType:Skill" + Proj,
            "namespace:{userPath}/Skill nodeType:Skill" + Proj,
            "path:{currentPath} scope:descendants nodeType:Skill" + Proj,
            "path:{nodeTypePath} scope:descendants nodeType:Skill" + Proj);
    }

    [Fact]
    public void PlatformDefaults_AreAlwaysFirst()
    {
        // The platform row is the only one guaranteed to resolve — every other targets a partition
        // that may not exist. Demoting it makes slash autocomplete surface nothing
        // (SkillAutocompleteTest). This is NOT a precedence statement: precedence is resolved from
        // each result's own partition, never from row order.
        AgentPickerProjection
            .BuildSkillQueries("rbuergi", "AgenticPension", "Office/Slide")
            .First().Should().Be(PlatformDefaults);
    }
}
