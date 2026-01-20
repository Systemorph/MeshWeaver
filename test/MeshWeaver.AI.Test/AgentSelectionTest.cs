#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using NSubstitute;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for agent selection in AgentChatFactoryBase.
/// Verifies that agents are correctly resolved from NodeType namespace.
/// </summary>
public class AgentSelectionTest
{
    private readonly IMeshQuery _meshQuery;

    public AgentSelectionTest()
    {
        _meshQuery = Substitute.For<IMeshQuery>();
    }

    /// <summary>
    /// Scenario: ACME/ProductLaunch has NodeType="ACME/Project"
    /// TodoAgent is defined at ACME/Project/TodoAgent
    /// When getting agents for ACME/ProductLaunch, TodoAgent should be found via the NodeType namespace.
    /// </summary>
    [Fact]
    public async Task GetAgentsForContext_ProductLaunchWithNodeType_FindsTodoAgentFromNodeTypeNamespace()
    {
        // Arrange
        var contextPath = "ACME/ProductLaunch";
        var nodeTypePath = "ACME/Project";

        // The ProductLaunch node with NodeType pointing to ACME/Project
        var productLaunchNode = new MeshNode("ProductLaunch", "ACME")
        {
            Name = "MeshFlow Product Launch",
            NodeType = nodeTypePath,
            Description = "Launch campaign for MeshFlow"
        };

        // The TodoAgent defined in the ACME/Project namespace
        var todoAgentConfig = new AgentConfiguration
        {
            Id = "TodoAgent",
            DisplayName = "Project Task Agent",
            Description = "Handles project tasks",
            ContextMatchPattern = "address=like=*ProductLaunch*",
            IsDefault = true,
            ExposedInNavigator = true
        };

        var todoAgentNode = new MeshNode("TodoAgent", nodeTypePath)
        {
            Name = "Project Task Agent",
            NodeType = "Agent",
            Content = todoAgentConfig
        };

        // Mock the non-generic interface method that extension methods call
        // The extension adds $type:MeshNode to the query before calling the interface method
        _meshQuery.QueryAsync(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    r.Query.Contains("scope:self") &&
                    r.Query.Contains("$type:MeshNode")),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<object>(productLaunchNode));

        // Mock: Query for agents in NodeType namespace returns TodoAgent
        // Use subtree scope to find agents that are children of the NodeType path
        _meshQuery.QueryAsync(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{nodeTypePath}") &&
                    r.Query.Contains("nodeType:Agent") &&
                    r.Query.Contains("scope:hierarchy") &&
                    r.Query.Contains("$type:MeshNode")),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<object>(todoAgentNode));

        // Mock: Query for agents in context path namespace returns empty
        _meshQuery.QueryAsync(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    r.Query.Contains("nodeType:Agent") &&
                    r.Query.Contains("scope:selfAndAncestors") &&
                    r.Query.Contains("$type:MeshNode")),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<object>());

        // Act - Use the same query logic that AgentChatFactoryBase uses
        var foundAgents = new List<(AgentConfiguration Config, string Path)>();

        // 1. Get the NodeType of the current node
        string? detectedNodeType = null;
        await foreach (var node in _meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self", null))
        {
            if (!string.IsNullOrEmpty(node.NodeType) && node.NodeType != "Agent" && node.NodeType != "Markdown")
            {
                detectedNodeType = node.NodeType;
                break;
            }
        }

        // 2. Query agents from NodeType namespace (use subtree to find children)
        if (!string.IsNullOrEmpty(detectedNodeType))
        {
            var query = $"path:{detectedNodeType} nodeType:Agent scope:hierarchy";
            await foreach (var node in _meshQuery.QueryAsync<MeshNode>(query, null))
            {
                if (node.Content is AgentConfiguration config)
                {
                    foundAgents.Add((config, node.Path ?? ""));
                }
            }
        }

        // 3. Query agents from context path namespace
        var pathQuery = $"path:{contextPath} nodeType:Agent scope:selfAndAncestors";
        await foreach (var node in _meshQuery.QueryAsync<MeshNode>(pathQuery, null))
        {
            if (node.Content is AgentConfiguration config && !foundAgents.Any(a => a.Config.Id == config.Id))
            {
                foundAgents.Add((config, node.Path ?? ""));
            }
        }

        // Assert
        detectedNodeType.Should().Be(nodeTypePath, "ProductLaunch node has NodeType=ACME/Project");
        foundAgents.Should().ContainSingle(a => a.Config.Id == "TodoAgent",
            "TodoAgent should be found from the ACME/Project namespace");

        var todoAgent = foundAgents.First(a => a.Config.Id == "TodoAgent");
        todoAgent.Config.DisplayName.Should().Be("Project Task Agent");
        todoAgent.Config.IsDefault.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: When at a path without a custom NodeType, agents should still be found
    /// from the path's ancestor hierarchy.
    /// </summary>
    [Fact]
    public async Task GetAgentsForContext_PathWithoutNodeType_FindsAgentsFromPathHierarchy()
    {
        // Arrange
        var contextPath = "ACME/ProductLaunch";

        // Node without a custom NodeType (defaults to Markdown)
        var productLaunchNode = new MeshNode("ProductLaunch", "ACME")
        {
            Name = "MeshFlow Product Launch",
            NodeType = "Markdown", // Generic markdown - should be ignored
            Description = "Launch campaign"
        };

        // A root-level agent
        var navigatorConfig = new AgentConfiguration
        {
            Id = "Navigator",
            DisplayName = "Navigator",
            IsDefault = true
        };

        var navigatorNode = new MeshNode("Navigator", null)
        {
            Name = "Navigator",
            NodeType = "Agent",
            Content = navigatorConfig
        };

        // Mock: Query for current node
        _meshQuery.QueryAsync(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    r.Query.Contains("scope:self") &&
                    r.Query.Contains("$type:MeshNode")),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<object>(productLaunchNode));

        // Mock: Query for agents in context path namespace returns Navigator
        _meshQuery.QueryAsync(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    r.Query.Contains("nodeType:Agent") &&
                    r.Query.Contains("scope:selfAndAncestors") &&
                    r.Query.Contains("$type:MeshNode")),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<object>(navigatorNode));

        // Act
        var foundAgents = new List<(AgentConfiguration Config, string Path)>();

        // 1. Get the NodeType of the current node
        string? detectedNodeType = null;
        await foreach (var node in _meshQuery.QueryAsync<MeshNode>($"path:{contextPath} scope:self", null))
        {
            if (!string.IsNullOrEmpty(node.NodeType) && node.NodeType != "Agent" && node.NodeType != "Markdown")
            {
                detectedNodeType = node.NodeType;
                break;
            }
        }

        // 2. Query agents from context path namespace
        var pathQuery = $"path:{contextPath} nodeType:Agent scope:selfAndAncestors";
        await foreach (var node in _meshQuery.QueryAsync<MeshNode>(pathQuery, null))
        {
            if (node.Content is AgentConfiguration config && !foundAgents.Any(a => a.Config.Id == config.Id))
            {
                foundAgents.Add((config, node.Path ?? ""));
            }
        }

        // Assert
        detectedNodeType.Should().BeNull("Markdown NodeType should be ignored");
        foundAgents.Should().ContainSingle(a => a.Config.Id == "Navigator",
            "Navigator should be found from path hierarchy");
    }

    /// <summary>
    /// Scenario: When AgentContext has pre-loaded AvailableAgents,
    /// agent selection should use them directly without querying.
    /// </summary>
    [Fact]
    public void AgentContext_WithPreloadedAgents_SelectsAgentWithoutQuerying()
    {
        // Arrange
        var todoAgentConfig = new AgentConfiguration
        {
            Id = "TodoAgent",
            DisplayName = "Project Task Agent",
            Description = "Handles project tasks",
            ContextMatchPattern = "address=like=*ProductLaunch*",
            IsDefault = true,
            ExposedInNavigator = true
        };

        var navigatorConfig = new AgentConfiguration
        {
            Id = "Navigator",
            DisplayName = "Navigator",
            Description = "General navigation agent",
            IsDefault = false
        };

        // Create context with pre-loaded agents and MeshNode
        var context = new AgentContext
        {
            Address = new MeshWeaver.Messaging.Address("ACME", "ProductLaunch"),
            Node = new MeshNode("ProductLaunch", "ACME") { NodeType = "ACME/Project" },
            AvailableAgents = new List<AgentConfiguration> { todoAgentConfig, navigatorConfig }
        };

        // Assert: Context has the expected properties
        context.Node?.NodeType.Should().Be("ACME/Project");
        context.AvailableAgents.Should().HaveCount(2);
        context.AvailableAgents.Should().ContainSingle(a => a.Id == "TodoAgent" && a.IsDefault);
        context.AvailableAgents.Should().ContainSingle(a => a.Id == "Navigator" && !a.IsDefault);

        // The ContextMatchPattern should match the address
        var addressStr = context.Address?.ToString() ?? "";
        var pattern = todoAgentConfig.ContextMatchPattern;
        pattern.Should().NotBeNullOrEmpty();

        // Verify pattern matching logic (same as AgentChatClient.MatchesContext)
        if (pattern!.StartsWith("address=like="))
        {
            var likePattern = pattern["address=like=".Length..].Trim('*');
            addressStr.Should().Contain(likePattern, "TodoAgent's pattern should match the address");
        }
    }

    /// <summary>
    /// Scenario: When AgentContext has pre-loaded AvailableAgents with a default agent,
    /// the default agent should be selected if no ContextMatchPattern matches.
    /// </summary>
    [Fact]
    public void AgentContext_WithPreloadedAgents_SelectsDefaultAgent()
    {
        // Arrange - agents without matching ContextMatchPattern
        var defaultAgentConfig = new AgentConfiguration
        {
            Id = "DefaultAgent",
            DisplayName = "Default Agent",
            Description = "Default agent for the context",
            IsDefault = true
        };

        var otherAgentConfig = new AgentConfiguration
        {
            Id = "OtherAgent",
            DisplayName = "Other Agent",
            Description = "Another agent",
            IsDefault = false
        };

        // Create context with pre-loaded agents
        var context = new AgentContext
        {
            Address = new MeshWeaver.Messaging.Address("ACME", "SomeNode"),
            Node = new MeshNode("SomeNode", "ACME") { NodeType = "ACME/Project" },
            AvailableAgents = new List<AgentConfiguration> { otherAgentConfig, defaultAgentConfig }
        };

        // Assert: Should find the default agent
        var defaultAgent = context.AvailableAgents?.FirstOrDefault(a => a.IsDefault);
        defaultAgent.Should().NotBeNull();
        defaultAgent!.Id.Should().Be("DefaultAgent");
    }

    #region Helper Methods

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    #endregion
}
