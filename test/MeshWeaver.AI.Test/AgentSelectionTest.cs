#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Persistence;
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
    /// Scenario: ACME/ProductLaunch has NodeType="Demos/ACME/Project"
    /// TodoAgent is defined at ACME/Project/TodoAgent
    /// When getting agents for ACME/ProductLaunch, TodoAgent should be found via the NodeType namespace.
    /// </summary>
    [Fact]
    public async Task QueryAgentsAsync_ProductLaunchWithNodeType_FindsTodoAgentFromNodeTypeNamespace()
    {
        // Arrange
        var contextPath = "Demos/ACME/ProductLaunch";
        var nodeTypePath = "Demos/ACME/Project";

        // The ProductLaunch node with NodeType pointing to ACME/Project
        var productLaunchNode = new MeshNode("ProductLaunch", "ACME")
        {
            Name = "MeshFlow Product Launch",
            NodeType = nodeTypePath
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
        var contextPath = "Demos/ACME/ProductLaunch";

        // Node without a custom NodeType (defaults to Markdown)
        var productLaunchNode = new MeshNode("ProductLaunch", "ACME")
        {
            Name = "MeshFlow Product Launch",
            NodeType = "Markdown" // Generic markdown - should be ignored
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
    /// Tests agent ordering by Order then DisplayName.
    /// </summary>
    [Fact]
    public void OrderByRelevance_OrdersByOrderThenDisplayName()
    {
        // Arrange
        var agents = new List<AgentDisplayInfo>
        {
            new()
            {
                Name = "Navigator",
                Order = -1,
                Description = "Navigation",
                AgentConfiguration = new AgentConfiguration { Id = "Navigator", DisplayName = "Navigator" }
            },
            new()
            {
                Name = "TodoAgent",
                Order = -10, // Lower = first
                Description = "Project tasks",
                AgentConfiguration = new AgentConfiguration { Id = "TodoAgent", DisplayName = "Todo Agent" }
            },
            new()
            {
                Name = "ACMEAgent",
                Order = 0,
                Description = "ACME specific",
                AgentConfiguration = new AgentConfiguration { Id = "ACMEAgent", DisplayName = "ACME Agent" }
            }
        };

        // Act
        var ordered = AgentOrderingHelper.OrderByRelevance(agents, null, null);

        // Assert - Ordered by Order: -10, -1, 0
        ordered.Should().HaveCount(3);
        ordered[0].Name.Should().Be("TodoAgent", "Order -10 comes first");
        ordered[1].Name.Should().Be("Navigator", "Order -1 comes second");
        ordered[2].Name.Should().Be("ACMEAgent", "Order 0 comes last");
    }

    /// <summary>
    /// Scenario: When AgentContext has pre-loaded AvailableAgents,
    /// the ordering should work based on Order.
    /// </summary>
    [Fact]
    public void AgentContext_WithPreloadedAgents_OrdersByOrder()
    {
        // Arrange
        var todoAgentConfig = new AgentConfiguration
        {
            Id = "TodoAgent",
            DisplayName = "Project Task Agent",
            Description = "Handles project tasks",
            Order = -10, // Lower = first
            IsDefault = true,
            ExposedInNavigator = true
        };

        var navigatorConfig = new AgentConfiguration
        {
            Id = "Navigator",
            DisplayName = "Navigator",
            Description = "General navigation agent",
            Order = -1,
            IsDefault = false
        };

        // Convert to display info for ordering
        var displayInfos = new List<AgentDisplayInfo>
        {
            new()
            {
                Name = navigatorConfig.Id,
                Order = navigatorConfig.Order,
                Description = navigatorConfig.Description ?? "",
                AgentConfiguration = navigatorConfig
            },
            new()
            {
                Name = todoAgentConfig.Id,
                Order = todoAgentConfig.Order,
                Description = todoAgentConfig.Description ?? "",
                AgentConfiguration = todoAgentConfig
            }
        };

        // Act - Order by Order
        var ordered = AgentOrderingHelper.OrderByRelevance(displayInfos, null, null);

        // Assert - TodoAgent (-10) comes before Navigator (-1)
        ordered.Should().HaveCount(2);
        ordered[0].Name.Should().Be("TodoAgent", "Order -10 comes first");
        ordered[1].Name.Should().Be("Navigator", "Order -1 comes second");
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
