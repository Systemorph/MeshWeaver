#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
    private readonly IMeshService _meshQuery;

    public AgentSelectionTest()
    {
        _meshQuery = Substitute.For<IMeshService>();
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

        // Mock: Query for current node to get NodeType (no nodeType: filter in query).
        // Production calls IMeshService.ObserveQuery<MeshNode> directly â€” mock that
        // (extension methods like QueryAsync can't be intercepted by NSubstitute).
        _meshQuery.ObserveQuery<MeshNode>(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    !r.Query.Contains("nodeType:")))
            .Returns(InitialChange(productLaunchNode));

        // Mock: Query for agents in NodeType namespace returns TodoAgent
        _meshQuery.ObserveQuery<MeshNode>(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{nodeTypePath}") &&
                    r.Query.Contains("nodeType:Agent")))
            .Returns(InitialChange(todoAgentNode));

        // Mock: Query for agents in context path namespace returns empty
        _meshQuery.ObserveQuery<MeshNode>(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    r.Query.Contains("nodeType:Agent")))
            .Returns(InitialChange());

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
            NodeType = "Markdown" // Generic markdown - should be ignored
        };

        // A root-level agent
        var orchestratorConfig = new AgentConfiguration
        {
            Id = "Orchestrator",
            DisplayName = "Orchestrator",
            Description = "General navigation agent",
            IsDefault = true
        };

        var orchestratorNode = new MeshNode("Orchestrator", null)
        {
            Name = "Orchestrator",
            NodeType = "Agent",
            Content = orchestratorConfig
        };

        // Mock: Query for current node (no nodeType: filter in query).
        // Production calls IMeshService.ObserveQuery<MeshNode> directly â€” mock that
        // (extension methods like QueryAsync can't be intercepted by NSubstitute).
        _meshQuery.ObserveQuery<MeshNode>(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    !r.Query.Contains("nodeType:")))
            .Returns(InitialChange(productLaunchNode));

        // Mock: Query for agents in context path namespace returns Orchestrator
        _meshQuery.ObserveQuery<MeshNode>(
                Arg.Is<MeshQueryRequest>(r =>
                    r.Query.Contains($"path:{contextPath}") &&
                    r.Query.Contains("nodeType:Agent")))
            .Returns(InitialChange(orchestratorNode));

        // Act - Call the REAL AgentOrderingHelper implementation
        var detectedNodeType = await AgentOrderingHelper.GetNodeTypeAsync(_meshQuery, contextPath);
        var foundAgents = await AgentOrderingHelper.QueryAgentsAsync(_meshQuery, contextPath, detectedNodeType);

        // Assert
        detectedNodeType.Should().BeNull("Markdown NodeType should be ignored");
        foundAgents.Should().ContainSingle(a => a.Name == "Orchestrator",
            "Orchestrator should be found from path hierarchy");
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
                Name = "Orchestrator",
                Order = -1,
                Description = "Navigation",
                AgentConfiguration = new AgentConfiguration { Id = "Orchestrator", DisplayName = "Orchestrator" }
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
        ordered[1].Name.Should().Be("Orchestrator", "Order -1 comes second");
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

        var orchestratorConfig = new AgentConfiguration
        {
            Id = "Orchestrator",
            DisplayName = "Orchestrator",
            Description = "General navigation agent",
            Order = -1,
            IsDefault = false
        };

        // Convert to display info for ordering
        var displayInfos = new List<AgentDisplayInfo>
        {
            new()
            {
                Name = orchestratorConfig.Id,
                Order = orchestratorConfig.Order,
                Description = orchestratorConfig.Description ?? "",
                AgentConfiguration = orchestratorConfig
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

        // Assert - TodoAgent (-10) comes before Orchestrator (-1)
        ordered.Should().HaveCount(2);
        ordered[0].Name.Should().Be("TodoAgent", "Order -10 comes first");
        ordered[1].Name.Should().Be("Orchestrator", "Order -1 comes second");
    }

    #region Helper Methods

    /// <summary>
    /// Mock <see cref="IMeshService.ObserveQuery{T}"/> by returning a single
    /// Initial <see cref="QueryResultChange{T}"/>. Production code subscribes,
    /// takes the Initial, and processes <c>Items</c> â€” exactly what this fakes.
    /// </summary>
    private static IObservable<QueryResultChange<MeshNode>> InitialChange(params MeshNode[] items)
        => Observable.Return(new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Initial,
            Items = items,
        });

    #endregion
}
