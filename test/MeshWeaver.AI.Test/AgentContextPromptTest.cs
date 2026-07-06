#pragma warning disable CS1591

using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for <see cref="AgentContext.ToPromptContext"/> — the SINGLE shape shipped to the
/// agent's system prompt ("# Current Application Context"). Guards that the shipped context is
/// RESOLVED into the identity parts an agent needs to know where it is — <c>namespace</c>,
/// <c>id</c>, the node <c>path</c> (nodePath), the remaining <c>path</c>, and the optional query
/// <c>parameters</c> — so agents can never legitimately claim "I don't know the context".
/// The prompt (AgentChatClient) and this test call the exact same method, so they can't drift.
/// </summary>
public class AgentContextPromptTest
{
    [Fact]
    public void ToPromptContext_resolves_namespace_id_nodePath_path_and_parameters()
    {
        var context = new AgentContext
        {
            Address = new Address("PartnerRe", "AIConsulting"),
            Context = "PartnerRe/AIConsulting",
            Path = "Tasks/123",
            Parameters = new Dictionary<string, string> { ["from"] = "5", ["to"] = "8" },
            Node = new MeshNode("FinalReport", "PartnerRe/AIConsulting")
            {
                Name = "Final Report",
                NodeType = "Markdown",
            },
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(context.ToPromptContext()));
        var root = doc.RootElement;

        // Top level: the remaining path after the node + the optional query parameters.
        Assert.Equal("Tasks/123", root.GetProperty("path").GetString());
        Assert.Equal("5", root.GetProperty("parameters").GetProperty("from").GetString());
        Assert.Equal("8", root.GetProperty("parameters").GetProperty("to").GetString());

        // Node IDENTITY fully resolved: nodePath (path) + namespace + id + nodeType + name.
        var node = root.GetProperty("node");
        Assert.Equal("PartnerRe/AIConsulting/FinalReport", node.GetProperty("path").GetString());
        Assert.Equal("PartnerRe/AIConsulting", node.GetProperty("namespace").GetString());
        Assert.Equal("FinalReport", node.GetProperty("id").GetString());
        Assert.Equal("Markdown", node.GetProperty("nodeType").GetString());
        Assert.Equal("Final Report", node.GetProperty("name").GetString());
    }

    [Fact]
    public void ToPromptContext_without_node_still_ships_address_and_null_node()
    {
        var context = new AgentContext
        {
            Address = new Address("PartnerRe", "AIConsulting"),
            Context = "PartnerRe/AIConsulting",
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(context.ToPromptContext()));
        var root = doc.RootElement;

        Assert.False(string.IsNullOrEmpty(root.GetProperty("address").GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("node").ValueKind);
    }
}
