#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using NSubstitute;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for agent selection using AgentOrderingHelper.
/// Verifies that agents are correctly resolved from NodeType namespace.
/// These tests call the REAL AgentOrderingHelper implementation.
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
    public async Task QueryAgentsAsync_ProductLaunchWithNodeType_FindsTodoAgentFromNodeTypeNamespace()
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

        // Mock: Query for current node to get NodeType
        _meshQuery.QueryAsync(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    r.Query.Contains("scope:self") &&
                    r.Query.Contains("$type:MeshNode")),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<object>(productLaunchNode));

        // Mock: Query for agents in NodeType namespace returns TodoAgent
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

        // Act - Call the REAL AgentOrderingHelper implementation
        // First get the NodeType
        var detectedNodeType = await AgentOrderingHelper.GetNodeTypeAsync(_meshQuery, contextPath);

        // Then query agents with the detected NodeType
        var foundAgents = await AgentOrderingHelper.QueryAgentsAsync(_meshQuery, contextPath, detectedNodeType);

        // Assert
        detectedNodeType.Should().Be(nodeTypePath, "ProductLaunch node has NodeType=ACME/Project");
        foundAgents.Should().ContainSingle(a => a.Name == "TodoAgent",
            "TodoAgent should be found from the ACME/Project namespace");

        var todoAgent = foundAgents.First(a => a.Name == "TodoAgent");
        todoAgent.Description.Should().Be("Handles project tasks");
        todoAgent.AgentConfiguration.IsDefault.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: When at a path without a custom NodeType, agents should still be found
    /// from the path's ancestor hierarchy.
    /// </summary>
    [Fact]
    public async Task QueryAgentsAsync_PathWithoutNodeType_FindsAgentsFromPathHierarchy()
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
            Description = "General navigation agent",
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

        // Act - Call the REAL AgentOrderingHelper implementation
        var detectedNodeType = await AgentOrderingHelper.GetNodeTypeAsync(_meshQuery, contextPath);
        var foundAgents = await AgentOrderingHelper.QueryAgentsAsync(_meshQuery, contextPath, detectedNodeType);

        // Assert
        detectedNodeType.Should().BeNull("Markdown NodeType should be ignored");
        foundAgents.Should().ContainSingle(a => a.Name == "Navigator",
            "Navigator should be found from path hierarchy");
    }

    /// <summary>
    /// Tests agent ordering by path relevance.
    /// Agents in the NodeType namespace should be prioritized.
    /// </summary>
    [Fact]
    public void OrderByRelevance_NodeTypeNamespaceAgent_HasHigherPriority()
    {
        // Arrange
        var contextPath = "ACME/ProductLaunch";
        var nodeTypePath = "ACME/Project";

        var agents = new List<AgentDisplayInfo>
        {
            new()
            {
                Name = "TodoAgent",
                Path = "ACME/Project/TodoAgent", // In NodeType namespace
                Description = "Project tasks",
                AgentConfiguration = new AgentConfiguration { Id = "TodoAgent" }
            },
            new()
            {
                Name = "Navigator",
                Path = "Navigator", // Root level
                Description = "Navigation",
                AgentConfiguration = new AgentConfiguration { Id = "Navigator" }
            },
            new()
            {
                Name = "ACMEAgent",
                Path = "ACME/ACMEAgent", // In context ancestor namespace
                Description = "ACME specific",
                AgentConfiguration = new AgentConfiguration { Id = "ACMEAgent" }
            }
        };

        // Act - Call the REAL AgentOrderingHelper implementation
        var ordered = AgentOrderingHelper.OrderByRelevance(agents, contextPath, nodeTypePath);

        // Assert - TodoAgent should be first because it's in the NodeType namespace
        ordered.Should().HaveCount(3);
        ordered[0].Name.Should().Be("TodoAgent", "Agent in NodeType namespace has highest priority");
        ordered[1].Name.Should().Be("ACMEAgent", "Agent in context ancestor path is second");
        ordered[2].Name.Should().Be("Navigator", "Root-level agent is last");
    }

    /// <summary>
    /// Tests CalculatePathRelevance scoring.
    /// </summary>
    [Fact]
    public void CalculatePathRelevance_ReturnsCorrectScores()
    {
        var contextPath = "ACME/ProductLaunch";
        var nodeTypePath = "ACME/Project";

        // Agent in exact context namespace - highest score (1000)
        AgentOrderingHelper.CalculatePathRelevance("ACME/ProductLaunch/LocalAgent", contextPath, nodeTypePath)
            .Should().Be(1000);

        // Agent in exact NodeType namespace - second highest (500)
        AgentOrderingHelper.CalculatePathRelevance("ACME/Project/TodoAgent", contextPath, nodeTypePath)
            .Should().Be(500);

        // Agent in context ancestor - 200 minus hops
        AgentOrderingHelper.CalculatePathRelevance("ACME/ACMEAgent", contextPath, nodeTypePath)
            .Should().Be(199); // 200 - 1 hop

        // Root level agent - ancestor of context
        AgentOrderingHelper.CalculatePathRelevance("Navigator", contextPath, nodeTypePath)
            .Should().Be(198); // 200 - 2 hops

        // Agent not in any hierarchy - 0
        AgentOrderingHelper.CalculatePathRelevance("Other/SomeAgent", contextPath, nodeTypePath)
            .Should().Be(0);
    }

    /// <summary>
    /// Scenario: When AgentContext has pre-loaded AvailableAgents,
    /// the ordering should still work based on path relevance.
    /// </summary>
    [Fact]
    public void AgentContext_WithPreloadedAgents_OrdersByRelevance()
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

        // Convert to display info for ordering
        var displayInfos = context.AvailableAgents.Select(a => new AgentDisplayInfo
        {
            Name = a.Id,
            Path = a.Id == "TodoAgent" ? "ACME/Project/TodoAgent" : "Navigator",
            Description = a.Description ?? "",
            AgentConfiguration = a
        }).ToList();

        // Act - Order by relevance using REAL implementation
        var contextPath = context.Address?.ToString()?.TrimStart('/') ?? "";
        var nodeTypePath = context.Node?.NodeType?.TrimStart('/') ?? "";
        var ordered = AgentOrderingHelper.OrderByRelevance(displayInfos, contextPath, nodeTypePath);

        // Assert
        ordered.Should().HaveCount(2);
        ordered[0].Name.Should().Be("TodoAgent", "TodoAgent is in NodeType namespace, should be first");
        ordered[1].Name.Should().Be("Navigator", "Navigator is at root, should be second");
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
