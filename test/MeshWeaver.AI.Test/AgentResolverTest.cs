#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Services;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for AgentResolver hierarchical agent resolution.
/// </summary>
public class AgentResolverTest
{
    private readonly IPersistenceService _persistence;
    private readonly IMeshQuery _meshQuery;
    private readonly ILogger<AgentResolver> _logger;
    private readonly AgentResolver _resolver;

    public AgentResolverTest()
    {
        _persistence = Substitute.For<IPersistenceService>();
        _meshQuery = Substitute.For<IMeshQuery>();
        _logger = Substitute.For<ILogger<AgentResolver>>();
        _resolver = new AgentResolver(_persistence, _meshQuery, _logger);
    }

    [Fact]
    public async Task GetAgentsForContext_RootLevel_ReturnsRootAgents()
    {
        // Arrange
        var rootAgent = CreateAgentNode("MeshNavigator", null, new AgentConfiguration
        {
            Id = "MeshNavigator",
            DisplayName = "Mesh Navigator",
            Description = "Default navigator agent",
            IsDefault = true
        });

        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("nodeType:Agent") && s.Contains("scope:children")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(rootAgent));

        // Act
        var agents = await _resolver.GetAgentsForContextAsync(null, TestContext.Current.CancellationToken);

        // Assert
        agents.Should().HaveCount(1);
        agents[0].Id.Should().Be("MeshNavigator");
        agents[0].IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task GetAgentsForContext_NestedPath_ReturnsHierarchicalAgents()
    {
        // Arrange - Set up agents at different levels
        var rootAgent = CreateAgentNode("MeshNavigator", null, new AgentConfiguration
        {
            Id = "MeshNavigator",
            DisplayName = "Mesh Navigator",
            IsDefault = true,
            DisplayOrder = -100
        });

        var pricingAgent = CreateAgentNode("InsuranceAgent", "pricing", new AgentConfiguration
        {
            Id = "InsuranceAgent",
            DisplayName = "Insurance Agent",
            Description = "Handles insurance pricings",
            DisplayOrder = 0
        });

        // Root level - matches queries without path: prefix or with empty path
        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("nodeType:Agent") && !s.Contains("path:")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(rootAgent));

        // Pricing level
        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("path:pricing") && s.Contains("nodeType:Agent")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(pricingAgent));

        // pricing/MS-2024 level (no agents)
        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("path:pricing/MS-2024")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable());

        // Act
        var agents = await _resolver.GetAgentsForContextAsync("pricing/MS-2024", TestContext.Current.CancellationToken);

        // Assert - Should get agents from all levels, ordered by DisplayOrder
        agents.Should().HaveCount(2);
        agents[0].Id.Should().Be("MeshNavigator"); // DisplayOrder = -100
        agents[1].Id.Should().Be("InsuranceAgent"); // DisplayOrder = 0
    }

    [Fact]
    public async Task GetAgentsForContext_OverrideAtLowerLevel_LowerLevelWins()
    {
        // Arrange - Same agent Id at different levels
        var rootMeshAgent = CreateAgentNode("MeshAgent", null, new AgentConfiguration
        {
            Id = "MeshAgent",
            Description = "Root level mesh agent",
            DisplayOrder = 0
        });

        var overriddenMeshAgent = CreateAgentNode("MeshAgent", "custom", new AgentConfiguration
        {
            Id = "MeshAgent",
            Description = "Custom mesh agent override",
            DisplayOrder = 0
        });

        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("nodeType:Agent") && !s.Contains("path:")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(rootMeshAgent));

        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("path:custom")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(overriddenMeshAgent));

        // Act
        var agents = await _resolver.GetAgentsForContextAsync("custom", TestContext.Current.CancellationToken);

        // Assert - Should only have one MeshAgent (from custom level)
        agents.Should().HaveCount(1);
        agents[0].Id.Should().Be("MeshAgent");
        agents[0].Description.Should().Be("Custom mesh agent override");
    }

    [Fact]
    public async Task GetDefaultAgent_ReturnsAgentWithIsDefaultTrue()
    {
        // Arrange
        var regularAgent = CreateAgentNode("RegularAgent", null, new AgentConfiguration
        {
            Id = "RegularAgent",
            IsDefault = false
        });

        var defaultAgent = CreateAgentNode("DefaultAgent", null, new AgentConfiguration
        {
            Id = "DefaultAgent",
            IsDefault = true
        });

        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("nodeType:Agent")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(regularAgent, defaultAgent));

        // Act
        var agent = await _resolver.GetDefaultAgentAsync(null, TestContext.Current.CancellationToken);

        // Assert
        agent.Should().NotBeNull();
        agent!.Id.Should().Be("DefaultAgent");
        agent.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task GetExposedAgents_ReturnsOnlyExposedAgents()
    {
        // Arrange
        var exposedAgent = CreateAgentNode("ExposedAgent", null, new AgentConfiguration
        {
            Id = "ExposedAgent",
            ExposedInNavigator = true
        });

        var hiddenAgent = CreateAgentNode("HiddenAgent", null, new AgentConfiguration
        {
            Id = "HiddenAgent",
            ExposedInNavigator = false
        });

        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("nodeType:Agent")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(exposedAgent, hiddenAgent));

        // Act
        var agents = await _resolver.GetExposedAgentsAsync(null, TestContext.Current.CancellationToken);

        // Assert
        agents.Should().HaveCount(1);
        agents[0].Id.Should().Be("ExposedAgent");
    }

    [Fact]
    public async Task GetAgent_ByPath_ReturnsSpecificAgent()
    {
        // Arrange
        var agent = CreateAgentNode("TodoAgent", "app/Todo", new AgentConfiguration
        {
            Id = "TodoAgent",
            DisplayName = "Todo Agent"
        });

        _persistence.GetNodeAsync("app/Todo/TodoAgent", Arg.Any<CancellationToken>())
            .Returns(agent);

        // Act
        var result = await _resolver.GetAgentAsync("app/Todo/TodoAgent", ct: TestContext.Current.CancellationToken);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("TodoAgent");
        result.DisplayName.Should().Be("Todo Agent");
    }

    [Fact]
    public async Task FindMatchingAgents_WithContextPattern_ReturnsMatchingAgents()
    {
        // Arrange
        var pricingAgent = CreateAgentNode("InsuranceAgent", null, new AgentConfiguration
        {
            Id = "InsuranceAgent",
            ContextMatchPattern = "address.type==pricing"
        });

        var todoAgent = CreateAgentNode("TodoAgent", null, new AgentConfiguration
        {
            Id = "TodoAgent",
            ContextMatchPattern = "address=like=*Todo*"
        });

        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("nodeType:Agent")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(pricingAgent, todoAgent));

        var context = new AgentContext
        {
            Address = new Address("pricing", "MS-2024")
        };

        // Act
        var agents = await _resolver.FindMatchingAgentsAsync(context, null, TestContext.Current.CancellationToken);

        // Assert
        agents.Should().HaveCount(1);
        agents[0].Id.Should().Be("InsuranceAgent");
    }

    [Fact]
    public async Task GetAgentsForContext_OrderedByDisplayOrderThenId()
    {
        // Arrange
        var agentB = CreateAgentNode("AgentB", null, new AgentConfiguration
        {
            Id = "AgentB",
            DisplayOrder = 10
        });

        var agentA = CreateAgentNode("AgentA", null, new AgentConfiguration
        {
            Id = "AgentA",
            DisplayOrder = 10
        });

        var agentC = CreateAgentNode("AgentC", null, new AgentConfiguration
        {
            Id = "AgentC",
            DisplayOrder = -5
        });

        _meshQuery.QueryAsync<MeshNode>(Arg.Is<string>(s => s.Contains("nodeType:Agent")), ct: Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(agentB, agentA, agentC));

        // Act
        var agents = await _resolver.GetAgentsForContextAsync(null, TestContext.Current.CancellationToken);

        // Assert
        agents.Should().HaveCount(3);
        agents[0].Id.Should().Be("AgentC"); // DisplayOrder = -5
        agents[1].Id.Should().Be("AgentA"); // DisplayOrder = 10, alphabetically first
        agents[2].Id.Should().Be("AgentB"); // DisplayOrder = 10, alphabetically second
    }

    #region Helper Methods

    private static MeshNode CreateAgentNode(string id, string? ns, AgentConfiguration config)
    {
        return new MeshNode(id, ns)
        {
            Name = config.DisplayName ?? id,
            Description = config.Description,
            NodeType = AgentResolver.AgentNodeType,
            Content = config
        };
    }

    private static async IAsyncEnumerable<MeshNode> ToAsyncEnumerable(params MeshNode[] nodes)
    {
        foreach (var node in nodes)
        {
            await Task.Yield();
            yield return node;
        }
    }

    #endregion
}
