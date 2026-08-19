using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins that the agent/model roster projections read <see cref="MeshNode.Content"/> in WHATEVER
/// shape it arrives — issue #1853.
///
/// <para><b>The bug.</b> <c>start_thread agentName:Voice</c> failed with "Selected agent 'Voice' was
/// not found among the available agents" while the error's own list showed every OTHER agent in the
/// same <c>{user}/Agent</c> namespace. It survived 20 minutes and an <c>update</c> of the node;
/// <c>recycle</c> healed it in seconds.</para>
///
/// <para>That reads like a stale cache, and it is not one. The query returned the node the whole
/// time — the PROJECTION dropped it. <c>ToAgentDisplayInfo</c> matched content against exactly two
/// shapes, a typed <c>AgentConfiguration</c> or a <c>JsonElement</c>, with <c>_ =&gt; null</c> for
/// everything else, and a null projection silently removes the agent from the roster. A node
/// written through a builder or the MCP <c>create</c> path carries its content as a
/// <c>JsonObject</c> — the as-written DOM, which is neither of those two — so it fell into the
/// default arm. <c>recycle</c> "fixed" it only because tearing the hub down forces the node to be
/// re-read from storage, where the content comes back as a <c>JsonElement</c>.</para>
///
/// <para>🚨 This is the framework's documented trap-door: <c>MeshNodeContentExtensions.ContentAs</c>
/// says so in its own summary — "<c>node.Content is T</c> / <c>as T</c> is the trap-door and yields
/// a silent null whenever the content arrives as untyped JSON, as the as-written DOM, or typed by a
/// different build of the same record". The projections now go through <c>ContentAs</c>, which
/// handles all of those.</para>
/// </summary>
public class AgentRosterContentShapeTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static MeshNode AgentNodeWith(object content) =>
        MeshNode.FromPath("rbuergi/Agent/Voice") with
        {
            Name = "Voice",
            NodeType = AgentNodeType.NodeType,
            Content = content,
        };

    private static JsonObject AgentDom() => new()
    {
        ["id"] = "Voice",
        ["description"] = "Speaks.",
        ["instructions"] = "Speak.",
    };

    /// <summary>
    /// 🚨 THE regression. A JsonObject is what a node builder / the MCP create path leaves on the
    /// node; before the fix this projected to null and the agent vanished from the roster with
    /// nothing logged and nothing failing.
    /// </summary>
    [Fact]
    public void Agent_WhoseContentIsTheAsWrittenDom_StillProjects()
    {
        var info = AgentPickerProjection.ToAgentDisplayInfo(AgentNodeWith(AgentDom()), Options);

        Assert.NotNull(info);
        Assert.Equal("Voice", info!.Name);
        Assert.Equal("rbuergi/Agent/Voice", info.Path);
        Assert.Equal("Voice", info.AgentConfiguration.Id);
    }

    /// <summary>Control: already-typed content keeps working (the common path).</summary>
    [Fact]
    public void Agent_WithTypedContent_StillProjects()
    {
        var info = AgentPickerProjection.ToAgentDisplayInfo(
            AgentNodeWith(new AgentConfiguration { Id = "Voice", Instructions = "Speak." }), Options);

        Assert.NotNull(info);
        Assert.Equal("Voice", info!.AgentConfiguration.Id);
    }

    /// <summary>Control: a JsonElement — the shape a node re-read from storage arrives in.</summary>
    [Fact]
    public void Agent_WithJsonElementContent_StillProjects()
    {
        using var doc = JsonDocument.Parse(AgentDom().ToJsonString());
        var info = AgentPickerProjection.ToAgentDisplayInfo(
            AgentNodeWith(doc.RootElement.Clone()), Options);

        Assert.NotNull(info);
        Assert.Equal("Voice", info!.AgentConfiguration.Id);
    }

    /// <summary>
    /// Content that is genuinely not an agent must still project to null — the tolerance is about
    /// the CARRIER shape, never about accepting the wrong payload. Without this the fix could
    /// "pass" by making everything non-null.
    /// </summary>
    [Fact]
    public void Node_WhoseContentIsNotAnAgent_StillProjectsToNull()
    {
        Assert.Null(AgentPickerProjection.ToAgentDisplayInfo(AgentNodeWith("just a string"), Options));
        Assert.Null(AgentPickerProjection.ToAgentDisplayInfo(
            MeshNode.FromPath("rbuergi/Agent/Empty") with { NodeType = AgentNodeType.NodeType }, Options));
    }

    /// <summary>
    /// The model picker's projection carries the identical two-arm switch, from the same file, and
    /// would fail the same way for a model node written as a DOM. Fixed together so the next reader
    /// does not have to rediscover it.
    /// </summary>
    [Fact]
    public void Model_WhoseContentIsTheAsWrittenDom_StillProjects()
    {
        var node = MeshNode.FromPath("Provider/OpenRouter/z-ai/glm-5.2") with
        {
            Name = "GLM 5.2",
            NodeType = LanguageModelNodeType.NodeType,
            Content = new JsonObject
            {
                ["id"] = "z-ai/glm-5.2",
                ["displayName"] = "GLM 5.2",
                ["provider"] = "OpenRouter",
            },
        };

        var info = AgentPickerProjection.ToModelInfo(node, Options);

        Assert.NotNull(info);
        Assert.Equal("Provider/OpenRouter/z-ai/glm-5.2", info!.Path);
        Assert.Equal("GLM 5.2", info.Label);
    }
}
