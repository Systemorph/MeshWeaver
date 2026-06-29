#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the fix for the "ThreadNamer answers a user's <c>hi</c> with <c>Name: …\nId: …</c>" leak:
/// background GENERATOR agents (<c>modelTier: utility</c> — ThreadNamer, NodeInitializer,
/// DescriptionWriter) must be hidden from every CONVERSATIONAL surface (the chat agent picker,
/// <c>/agent</c>, <c>@</c>-references) — the exact filter the chat applies in
/// <c>ThreadChatView.OnAgentList</c>.
///
/// <para><b>But not from <see cref="AgentPickerProjection.ProjectAgents"/> itself</b>: the
/// generators build their own <see cref="AgentChatClient"/> and
/// <c>SetSelectedAgent("NodeInitializer"/"DescriptionWriter")</c>, so the projection must keep
/// utility agents resolvable. Filtering in the projection would break icon/description generation —
/// these tests guard both halves.</para>
/// </summary>
public class AgentPickerUtilityFilterTest
{
    private static readonly JsonSerializerOptions Json = new();

    private static MeshNode AgentNode(string id, string? modelTier = null, bool isDefault = false) =>
        new(id, AgentPickerProjection.AgentRootNamespace)
        {
            NodeType = AgentNodeType.NodeType,
            Name = id,
            Content = new AgentConfiguration
            {
                Id = id,
                ModelTier = modelTier,
                IsDefault = isDefault,
            },
        };

    [Fact]
    public void IsUtilityAgent_TrueOnlyForUtilityTier()
    {
        AgentDisplayInfo Make(string id, string? tier) => new()
        {
            Name = id,
            Description = "",
            AgentConfiguration = new AgentConfiguration { Id = id, ModelTier = tier },
        };

        AgentPickerProjection.IsUtilityAgent(Make("ThreadNamer", "utility")).Should().BeTrue();
        AgentPickerProjection.IsUtilityAgent(Make("ThreadNamer", "UTILITY")).Should().BeTrue("the tier match is case-insensitive");
        AgentPickerProjection.IsUtilityAgent(Make("NotificationTriage", "light")).Should().BeFalse();
        AgentPickerProjection.IsUtilityAgent(Make("Assistant", null)).Should().BeFalse();
    }

    [Fact]
    public void ProjectAgents_KeepsUtilityAgents_SoGeneratorsCanStillResolveThem()
    {
        // ProjectAgents is the shared projection the GENERATORS (IconGenerator → NodeInitializer,
        // DescriptionGenerator → DescriptionWriter) rely on — it must NOT drop utility agents.
        var snapshot = new[]
        {
            AgentNode("Assistant", isDefault: true),
            AgentNode("ThreadNamer", modelTier: "utility"),
            AgentNode("NodeInitializer", modelTier: "utility"),
        };

        var projected = AgentPickerProjection.ProjectAgents(snapshot, Json);

        projected.Select(a => a.Name).Should().Contain(new[] { "Assistant", "ThreadNamer", "NodeInitializer" },
            "the projection feeds the programmatic generators; it must keep utility agents");
    }

    [Fact]
    public void ConversationalFilter_ExcludesUtilityAgents_KeepsRealAgents()
    {
        // The chat UI (ThreadChatView.OnAgentList) applies exactly this Where over the projection
        // before populating the /agent picker + @-reference map.
        var snapshot = new[]
        {
            AgentNode("Assistant", isDefault: true), // exposedInNavigator:false, but the conversational default
            AgentNode("Coder"),
            AgentNode("ThreadNamer", modelTier: "utility"),
            AgentNode("DescriptionWriter", modelTier: "utility"),
        };

        var conversational = AgentPickerProjection.ProjectAgents(snapshot, Json)
            .Where(a => !AgentPickerProjection.IsUtilityAgent(a))
            .Select(a => a.Name)
            .ToList();

        conversational.Should().Contain(new[] { "Assistant", "Coder" });
        conversational.Should().NotContain("ThreadNamer",
            "a utility generator must never be conversationally selectable");
        conversational.Should().NotContain("DescriptionWriter");
    }
}
